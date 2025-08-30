// TradingConsole.Wpf/Services/Analysis/ThesisSynthesizer.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TradingConsole.Core.Models;
using TradingConsole.Wpf.Services.Analysis;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Wpf.Services
{
    public class ThesisSynthesizer
    {
        private readonly SettingsViewModel _settingsViewModel;
        private readonly SignalLoggerService _signalLoggerService;
        private readonly NotificationService _notificationService;
        private readonly AnalysisStateManager _stateManager;

        public ThesisSynthesizer(SettingsViewModel settingsViewModel, SignalLoggerService signalLoggerService, NotificationService notificationService, AnalysisStateManager stateManager)
        {
            _settingsViewModel = settingsViewModel;
            _signalLoggerService = signalLoggerService;
            _notificationService = notificationService;
            _stateManager = stateManager;
        }

        public void SynthesizeTradeSignal(AnalysisResult result)
        {
            if (result.InstrumentGroup != "INDEX") return;

            MarketThesis baseThesis = UpdateIntradayThesis(result);

            var (bullDrivers, bearDrivers, conviction, isChoppy, finalThesis) = CalculateConfluenceScore(result, baseThesis);
            result.MarketThesis = finalThesis;
            result.BullishDrivers = bullDrivers;
            result.BearishDrivers = bearDrivers;

            if (_stateManager.CurrentMarketPhase == MarketPhase.Opening)
            {
                conviction = (int)Math.Round(conviction * 0.5); // Reduce conviction during open
            }

            conviction = ApplyTrendFilter(result, conviction);
            result.ConvictionScore = conviction;

            string playbook;
            if (isChoppy)
            {
                playbook = "Choppy / Conflicting Signals";
            }
            else if (result.MarketThesis == MarketThesis.Bullish_Breakout_Attempt) playbook = "Bullish Breakout Attempt";
            else if (result.MarketThesis == MarketThesis.Bearish_Breakdown_Attempt) playbook = "Bearish Breakdown Attempt";
            else if (conviction >= 7) playbook = "Strong Bullish Conviction";
            else if (conviction >= 3) playbook = "Moderate Bullish Conviction";
            else if (conviction <= -7) playbook = "Strong Bearish Conviction";
            else if (conviction <= -3) playbook = "Moderate Bearish Conviction";
            else playbook = "Neutral / Observe";

            string newPrimarySignal = "Neutral";
            if (!isChoppy)
            {
                if (conviction >= 3) newPrimarySignal = "Bullish";
                else if (conviction <= -3) newPrimarySignal = "Bearish";
            }

            // --- NEW LOGIC: Latch the Active Thesis on a high-conviction signal ---
            // If we get a new high-conviction signal, we set it as the active thesis.
            if ((conviction >= 7 && result.ActiveThesis != "Bullish") ||
                (conviction <= -7 && result.ActiveThesis != "Bearish"))
            {
                result.ActiveThesis = newPrimarySignal;
                result.ActiveThesisEntryPrice = result.LTP;
            }
            // If conviction drops back to neutral, we reset the thesis.
            else if (conviction > -3 && conviction < 3)
            {
                result.ActiveThesis = "Neutral";
                result.ActiveThesisEntryPrice = 0;
            }

            string oldPrimarySignal = result.PrimarySignal;
            result.PrimarySignal = newPrimarySignal;
            result.FinalTradeSignal = playbook;
            result.MarketNarrative = GenerateMarketNarrative(result);

            if (result.PrimarySignal != oldPrimarySignal && oldPrimarySignal != "Initializing")
            {
                if (_stateManager.LastSignalTime.TryGetValue(result.SecurityId, out var lastTime) && (DateTime.UtcNow - lastTime).TotalSeconds < 60)
                {
                    return;
                }
                _stateManager.LastSignalTime[result.SecurityId] = DateTime.UtcNow;

                _signalLoggerService.LogSignal(result);
                Task.Run(() => _notificationService.SendTelegramSignalAsync(result, oldPrimarySignal));
            }
        }

        private int ApplyTrendFilter(AnalysisResult result, int currentConviction)
        {
            bool isAtSupport = result.CustomLevelSignal == "At Key Support" || result.DayRangeSignal == "Near Low" || result.VwapBandSignal == "At Lower Band";
            bool isAtResistance = result.CustomLevelSignal == "At Key Resistance" || result.DayRangeSignal == "Near High" || result.VwapBandSignal == "At Upper Band";

            bool strongBullishIntradayTrend = result.PriceVsVwapSignal == "Above VWAP" && result.EmaSignal5Min == "Bullish Cross";
            bool strongBearishIntradayTrend = result.PriceVsVwapSignal == "Below VWAP" && result.EmaSignal5Min == "Bearish Cross";

            if (result.MarketStructure == "Trending Up")
            {
                if (currentConviction < 0 && !strongBearishIntradayTrend) return 0;
                if (currentConviction > 0 && isAtSupport) return currentConviction + 2;
            }

            if (result.MarketStructure == "Trending Down")
            {
                if (currentConviction > 0 && !strongBullishIntradayTrend) return 0;
                if (currentConviction < 0 && isAtResistance) return currentConviction - 2;
            }

            if (strongBullishIntradayTrend && currentConviction > 0)
            {
                return currentConviction + 3;
            }

            if (strongBearishIntradayTrend && currentConviction < 0)
            {
                return currentConviction - 3;
            }

            return currentConviction;
        }

        private (List<string> BullishDrivers, List<string> BearishDrivers, int Score, bool IsChoppy, MarketThesis FinalThesis) CalculateConfluenceScore(AnalysisResult r, MarketThesis baseThesis)
        {
            var bullDrivers = new List<string>();
            var bearDrivers = new List<string>();
            int bullScore = 0;
            int bearScore = 0;
            MarketThesis finalThesis = baseThesis;

            var driversToEvaluate = new List<SignalDriver>();

            // **IMPROVEMENT**: Use the dynamic MarketRegime to select the correct playbook (Trending vs. Range-Bound).
            switch (r.MarketRegime)
            {
                case "Intraday Trend Up":
                case "Intraday Trend Down":
                    driversToEvaluate.AddRange(_settingsViewModel.Strategy.TrendingBullDrivers);
                    driversToEvaluate.AddRange(_settingsViewModel.Strategy.TrendingBearDrivers);
                    break;
                case "Balancing":
                default:
                    // **IMPROVEMENT**: In a balancing market, we *only* use range-bound drivers.
                    // This was the intentional change that reduced the line count.
                    driversToEvaluate.AddRange(_settingsViewModel.Strategy.RangeBoundBullishDrivers);
                    driversToEvaluate.AddRange(_settingsViewModel.Strategy.RangeBoundBearishDrivers);
                    break;
            }

            if (r.MarketRegime == "High Volatility")
            {
                driversToEvaluate.AddRange(_settingsViewModel.Strategy.VolatileBullishDrivers);
                driversToEvaluate.AddRange(_settingsViewModel.Strategy.VolatileBearishDrivers);
            }

            driversToEvaluate = driversToEvaluate.Distinct().ToList();

            foreach (var driver in driversToEvaluate.Where(d => d.IsEnabled))
            {
                if (IsSignalActive(r, driver.Name))
                {
                    if (driver.Weight > 0) bullScore += driver.Weight;
                    else bearScore += driver.Weight;
                }
            }

            int initialScore = bullScore + bearScore;

            if (baseThesis == MarketThesis.Balancing)
            {
                bool ibBreakout = initialScore >= 5 && (r.InitialBalanceSignal == "IB Breakout" || r.InitialBalanceSignal == "IB Extension Up");
                bool ibBreakdown = initialScore <= -5 && (r.InitialBalanceSignal == "IB Breakdown" || r.InitialBalanceSignal == "IB Extension Down");
                bool vahBreakout = initialScore >= 5 && r.LTP > r.DevelopingVah && r.DevelopingVah > 0;
                bool valBreakdown = initialScore <= -5 && r.LTP < r.DevelopingVal && r.DevelopingVal > 0;

                if (ibBreakout || vahBreakout)
                {
                    finalThesis = MarketThesis.Bullish_Breakout_Attempt;
                    driversToEvaluate = _settingsViewModel.Strategy.BreakoutBullishDrivers.Concat(_settingsViewModel.Strategy.BreakoutBearishDrivers).ToList();
                    bullScore = 0;
                    bearScore = 0;
                    foreach (var driver in driversToEvaluate.Where(d => d.IsEnabled))
                    {
                        if (IsSignalActive(r, driver.Name))
                        {
                            if (driver.Weight > 0) bullScore += driver.Weight;
                            else bearScore += driver.Weight;
                        }
                    }
                }
                else if (ibBreakdown || valBreakdown)
                {
                    finalThesis = MarketThesis.Bearish_Breakdown_Attempt;
                    driversToEvaluate = _settingsViewModel.Strategy.BreakoutBullishDrivers.Concat(_settingsViewModel.Strategy.BreakoutBearishDrivers).ToList();
                    bullScore = 0;
                    bearScore = 0;
                    foreach (var driver in driversToEvaluate.Where(d => d.IsEnabled))
                    {
                        if (IsSignalActive(r, driver.Name))
                        {
                            if (driver.Weight > 0) bullScore += driver.Weight;
                            else bearScore += driver.Weight;
                        }
                    }
                }
            }

            foreach (var driver in driversToEvaluate.Where(d => d.IsEnabled))
            {
                if (IsSignalActive(r, driver.Name))
                {
                    if (driver.Weight > 0) bullDrivers.Add($"{driver.Name} (+{driver.Weight})");
                    else bearDrivers.Add($"{driver.Name} ({driver.Weight})");
                }
            }

            bool isChoppy = (bullScore >= 5 && Math.Abs(bearScore) >= 5) || r.GammaSignal == "Balanced OTM Gamma";

            int finalScore = bullScore + bearScore;
            return (bullDrivers, bearDrivers, finalScore, isChoppy, finalThesis);
        }

        private bool IsSignalActive(AnalysisResult r, string driverName)
        {
            var fiveMinCandles = _stateManager.GetCandles(r.SecurityId, TimeSpan.FromMinutes(5));
            Candle? lastFiveMinCandle = fiveMinCandles?.LastOrDefault();
            bool isBullishCandle = lastFiveMinCandle != null && lastFiveMinCandle.Close > lastFiveMinCandle.Open;
            bool isBearishCandle = lastFiveMinCandle != null && lastFiveMinCandle.Close < lastFiveMinCandle.Open;

            bool isBullishPattern = r.CandleSignal5Min.Contains("Bullish");
            bool isBearishPattern = r.CandleSignal5Min.Contains("Bearish");
            bool atSupport = r.DayRangeSignal == "Near Low" || r.VwapBandSignal == "At Lower Band" || r.MarketProfileSignal.Contains("VAL");
            bool atResistance = r.DayRangeSignal == "Near High" || r.VwapBandSignal == "At Upper Band" || r.MarketProfileSignal.Contains("VAH");
            bool volumeConfirmed = r.VolumeSignal == "Volume Burst";
            bool isNotInStrongTrend = r.MarketThesis != MarketThesis.Bullish_Trend && r.MarketThesis != MarketThesis.Bearish_Trend;

            switch (driverName)
            {
                case "Aggressive Buying Pressure": return r.MicroFlowSignal == "Aggressive Buying";
                case "Aggressive Selling Pressure": return r.MicroFlowSignal == "Aggressive Selling";

                case "Option Breakout Setup": return r.VolatilityStateSignal == "IV Squeeze Setup";
                case "Range Contraction": return r.AtrSignal5Min == "Vol Contracting";

                case "True Acceptance Above Y-VAH": return r.MarketProfileSignal == "True Acceptance Above Y-VAH";
                case "True Acceptance Below Y-VAL": return r.MarketProfileSignal == "True Acceptance Below Y-VAL";
                case "Look Above and Fail at Y-VAH": return r.MarketProfileSignal == "Look Above and Fail at Y-VAH";
                case "Look Below and Fail at Y-VAL": return r.MarketProfileSignal == "Look Below and Fail at Y-VAL";
                case "Initiative Buying Above Y-VAH": return r.MarketProfileSignal == "Initiative Buying Above Y-VAH";
                case "Initiative Selling Below Y-VAL": return r.MarketProfileSignal == "Initiative Selling Below Y-VAL";
                case "IB breakout is extending": return r.InitialBalanceSignal == "IB Extension Up";
                case "IB breakdown is extending": return r.InitialBalanceSignal == "IB Extension Down";

                case "Price above VWAP": return r.PriceVsVwapSignal == "Above VWAP";
                case "Price below VWAP": return r.PriceVsVwapSignal == "Below VWAP";
                case "5m EMA confirms bullish trend": return r.EmaSignal5Min == "Bullish Cross";
                case "5m EMA confirms bearish trend": return r.EmaSignal5Min == "Bearish Cross";
                case "5m VWAP EMA confirms bullish trend": return r.VwapEmaSignal5Min == "Bullish Cross";
                case "5m VWAP EMA confirms bearish trend": return r.VwapEmaSignal5Min == "Bearish Cross";
                case "OI confirms new longs": return r.OiSignal == "Long Buildup";
                case "OI confirms new shorts": return r.OiSignal == "Short Buildup";
                case "High OTM Call Gamma": return r.GammaSignal == "High OTM Call Gamma";
                case "High OTM Put Gamma": return r.GammaSignal == "High OTM Put Gamma";
                case "Institutional Intent is Bullish": return r.InstitutionalIntent.Contains("Bullish");
                case "Institutional Intent is Bearish": return r.InstitutionalIntent.Contains("Bearish");

                case "Bullish Pattern with Volume Confirmation": return isBullishPattern && volumeConfirmed;
                case "Bearish Pattern with Volume Confirmation": return isBearishPattern && volumeConfirmed;
                case "Bullish Pattern at Key Support": return isBullishPattern && atSupport;
                case "Bearish Pattern at Key Resistance": return isBearishPattern && atResistance;
                case "Bullish Skew Divergence (Full)": return r.IvSkewSignal == "Bullish Skew Divergence (Full)" && isNotInStrongTrend;
                case "Bearish Skew Divergence (Full)": return r.IvSkewSignal == "Bearish Skew Divergence (Full)" && isNotInStrongTrend;
                case "Bullish OBV Div at range low": return r.ObvDivergenceSignal5Min.Contains("Bullish") && atSupport && isNotInStrongTrend;
                case "Bearish OBV Div at range high": return r.ObvDivergenceSignal5Min.Contains("Bearish") && atResistance && isNotInStrongTrend;
                case "Bullish RSI Div at range low": return r.RsiSignal5Min.Contains("Bullish") && atSupport && isNotInStrongTrend;
                case "Bearish RSI Div at range high": return r.RsiSignal5Min.Contains("Bearish") && atResistance && isNotInStrongTrend;
                case "Low volume suggests exhaustion (Bullish)": return !volumeConfirmed && r.AtrSignal5Min == "Vol Contracting" && r.DayRangeSignal == "Near Low";
                case "Low volume suggests exhaustion (Bearish)": return !volumeConfirmed && r.AtrSignal5Min == "Vol Contracting" && r.DayRangeSignal == "Near High";
                case "Price Above GEX Flip Point (Bullish Hedging Flow)":
                    return r.GexFlipPoint > 0 && r.LTP > r.GexFlipPoint;
                case "Price Below GEX Flip Point (Bearish Hedging Flow)":
                    return r.GexFlipPoint > 0 && r.LTP < r.GexFlipPoint;
                case "Net GEX is Negative (Market Makers are Short Gamma, Volatility Amplified)":
                    return r.NetGex < 0;
                case "Net GEX is Positive (Volatility Dampened)":
                    return r.NetGex > 0;
                case "Price Approaching Max GEX Level (Pinning Risk)":
                    return r.MaxGexLevel > 0 && Math.Abs(r.LTP - r.MaxGexLevel) < (r.LTP * 0.001m); // Within 0.1% of Max GEX


                // **IMPROVEMENT**: Robust logic for Volume Burst, independent of VWAP.
                case "Bullish Breakout on Volume Burst": return volumeConfirmed && isBullishCandle;
                case "Bearish Breakdown on Volume Burst": return volumeConfirmed && isBearishCandle;
                case "Bullish Trend Continuation":
                    // This driver is only active if:
                    // 1. The established thesis is "Bullish".
                    // 2. The current price is still above the price where the thesis was established.
                    // 3. We still have confirming evidence (e.g., price is above VWAP).
                    return r.ActiveThesis == "Bullish" &&
                           r.LTP > r.ActiveThesisEntryPrice &&
                           r.PriceVsVwapSignal == "Above VWAP";

                case "Bearish Trend Continuation":
                    return r.ActiveThesis == "Bearish" &&
                           r.LTP < r.ActiveThesisEntryPrice &&
                           r.PriceVsVwapSignal == "Below VWAP";

                default: return false;
            }
        }

        private MarketThesis UpdateIntradayThesis(AnalysisResult result)
        {
            DominantPlayer player = DetermineDominantPlayer(result);
            result.DominantPlayer = player;
            if (result.MarketStructure == "Trending Up")
            {
                if (player == DominantPlayer.Buyers) return MarketThesis.Bullish_Trend;
                if (player == DominantPlayer.Sellers) return MarketThesis.Bullish_Rotation;
                return MarketThesis.Bullish_Trend;
            }
            if (result.MarketStructure == "Trending Down")
            {
                if (player == DominantPlayer.Sellers) return MarketThesis.Bearish_Trend;
                if (player == DominantPlayer.Buyers) return MarketThesis.Bearish_Rotation;
                return MarketThesis.Bearish_Trend;
            }
            return MarketThesis.Balancing;
        }

        private DominantPlayer DetermineDominantPlayer(AnalysisResult result)
        {
            int buyerScore = 0;
            int sellerScore = 0;

            if (result.PriceVsVwapSignal == "Above VWAP") buyerScore += 2;
            if (result.PriceVsVwapSignal == "Below VWAP") sellerScore += 2;
            if (result.LTP > result.DevelopingPoc && result.DevelopingPoc > 0) buyerScore += 1;
            if (result.LTP < result.DevelopingPoc && result.DevelopingPoc > 0) sellerScore += 1;

            if (result.EmaSignal5Min == "Bullish Cross") buyerScore += 1;
            if (result.EmaSignal5Min == "Bearish Cross") sellerScore += 1;
            if (result.RsiValue5Min > 60) buyerScore += 1;
            if (result.RsiValue5Min < 40) sellerScore += 1;

            if (result.OiSignal == "Long Buildup") buyerScore += 2;
            if (result.OiSignal == "Short Buildup") sellerScore += 2;
            if (result.OiSignal == "Short Covering") buyerScore += 1;
            if (result.OiSignal == "Long Unwinding") sellerScore += 1;

            if (buyerScore > sellerScore * 1.5) return DominantPlayer.Buyers;
            if (sellerScore > buyerScore * 1.5) return DominantPlayer.Sellers;

            return DominantPlayer.Balance;
        }

        private string GenerateMarketNarrative(AnalysisResult r) { return $"Thesis: {r.MarketThesis}. Dominant Player: {r.DominantPlayer}. Open: {r.OpenTypeSignal}. vs VWAP: {r.PriceVsVwapSignal}."; }
    }
}