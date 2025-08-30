// In TradingConsole.Wpf/Services/Analysis/ThesisSynthesizer.cs

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

            // This helper method is preserved to continue populating the "Dominant Player" field.
            UpdateIntradayThesis(result);

            // Determine the current playbook based on a prioritized set of rules.
            var (activeThesis, keyDrivers) = DetermineMarketPlaybook(result);
            result.MarketThesis = activeThesis;
            result.BullishDrivers = keyDrivers.Where(d => d.Weight > 0).Select(d => $"{d.Name} (+{d.Weight})").ToList();
            result.BearishDrivers = keyDrivers.Where(d => d.Weight < 0).Select(d => $"{d.Name} ({d.Weight})").ToList();

            // The conviction score is now a measure of the CONFIDENCE in the current playbook.
            int conviction = CalculateConfidenceScore(keyDrivers);
            if (_stateManager.CurrentMarketPhase == MarketPhase.Opening)
            {
                conviction = (int)Math.Round(conviction * 0.6); // Reduce confidence during volatile open
            }
            result.ConvictionScore = conviction;

            string newPrimarySignal = "Neutral";
            if (conviction >= 3) newPrimarySignal = "Bullish";
            else if (conviction <= -3) newPrimarySignal = "Bearish";

            // Latch the Active Thesis based on the playbook.
            if ((conviction >= 7 && result.ActiveThesis != activeThesis.ToString()) || (conviction <= -7 && result.ActiveThesis != activeThesis.ToString()))
            {
                result.ActiveThesis = activeThesis.ToString();
                result.ActiveThesisEntryPrice = result.LTP;
            }
            else if (conviction > -3 && conviction < 3)
            {
                result.ActiveThesis = "Neutral";
                result.ActiveThesisEntryPrice = 0;
            }

            string oldPrimarySignal = result.PrimarySignal;
            result.PrimarySignal = newPrimarySignal;
            result.FinalTradeSignal = activeThesis.ToString().Replace("_", " "); // User-friendly format
            result.MarketNarrative = GenerateMarketNarrative(result);

            // Trigger notifications on significant signal changes.
            if (result.PrimarySignal != oldPrimarySignal && oldPrimarySignal != "Initializing")
            {
                if (_stateManager.LastSignalTime.TryGetValue(result.SecurityId, out var lastTime) && (DateTime.UtcNow - lastTime).TotalSeconds < 60) return;

                _stateManager.LastSignalTime[result.SecurityId] = DateTime.UtcNow;
                _signalLoggerService.LogSignal(result);
                Task.Run(() => _notificationService.SendTelegramSignalAsync(result, oldPrimarySignal));
            }
        }

        private (MarketThesis, List<SignalDriver>) DetermineMarketPlaybook(AnalysisResult r)
        {
            // --- Reversal Playbooks (Highest Priority) ---
            var bullishReversalDrivers = GetActiveDrivers(r, _settingsViewModel.Strategy.RangeBoundBullishDrivers);
            if (bullishReversalDrivers.Any(d => d.Name.Contains("Pattern at Key Support")) && bullishReversalDrivers.Count >= 2)
            {
                return (MarketThesis.Bullish_Reversal_At_Support, bullishReversalDrivers);
            }

            var bearishReversalDrivers = GetActiveDrivers(r, _settingsViewModel.Strategy.RangeBoundBearishDrivers);
            if (bearishReversalDrivers.Any(d => d.Name.Contains("Pattern at Key Resistance")) && bearishReversalDrivers.Count >= 2)
            {
                return (MarketThesis.Bearish_Reversal_At_Resistance, bearishReversalDrivers);
            }

            // --- Breakout/Breakdown Playbooks ---
            var bullishBreakoutDrivers = GetActiveDrivers(r, _settingsViewModel.Strategy.BreakoutBullishDrivers);
            if (bullishBreakoutDrivers.Any() && (r.InitialBalanceSignal == "IB Breakout" || r.MarketProfileSignal.Contains("Acceptance Above")))
            {
                return (MarketThesis.Bullish_Breakout_Attempt, bullishBreakoutDrivers);
            }

            var bearishBreakdownDrivers = GetActiveDrivers(r, _settingsViewModel.Strategy.BreakoutBearishDrivers);
            if (bearishBreakdownDrivers.Any() && (r.InitialBalanceSignal == "IB Breakdown" || r.MarketProfileSignal.Contains("Acceptance Below")))
            {
                return (MarketThesis.Bearish_Breakdown_Attempt, bearishBreakdownDrivers);
            }

            // --- Trend Continuation Playbooks ---
            var bullishTrendDrivers = GetActiveDrivers(r, _settingsViewModel.Strategy.TrendingBullDrivers);
            if (r.MarketStructure == "Trending Up" && bullishTrendDrivers.Count > 2)
            {
                return (MarketThesis.Bullish_Trend_Continuation, bullishTrendDrivers);
            }

            var bearishTrendDrivers = GetActiveDrivers(r, _settingsViewModel.Strategy.TrendingBearDrivers);
            if (r.MarketStructure == "Trending Down" && bearishTrendDrivers.Count > 2)
            {
                return (MarketThesis.Bearish_Trend_Continuation, bearishTrendDrivers);
            }

            // --- Default / Fallback Playbooks ---
            if (r.MarketRegime == "High Volatility" || (bullishTrendDrivers.Any() && bearishTrendDrivers.Any()))
            {
                return (MarketThesis.High_Volatility_Choppy, bullishTrendDrivers.Concat(bearishTrendDrivers).ToList());
            }

            if (r.MarketStructure == "Balancing")
            {
                return (MarketThesis.Balancing_Range_Bound, new List<SignalDriver>());
            }

            return (MarketThesis.Indeterminate, new List<SignalDriver>());
        }

        private List<SignalDriver> GetActiveDrivers(AnalysisResult r, IEnumerable<SignalDriver> drivers)
        {
            return drivers.Where(d => d.IsEnabled && IsSignalActive(r, d.Name)).ToList();
        }

        private int CalculateConfidenceScore(List<SignalDriver> drivers)
        {
            if (drivers == null || !drivers.Any()) return 0;
            return drivers.Sum(d => d.Weight);
        }

        private bool IsSignalActive(AnalysisResult r, string driverName)
        {
            bool isBullishPattern = r.CandleSignal5Min.Contains("Bullish");
            bool isBearishPattern = r.CandleSignal5Min.Contains("Bearish");
            bool atSupport = r.DayRangeSignal == "Near Low" || r.VwapBandSignal == "At Lower Band" || r.MarketProfileSignal.Contains("VAL");
            bool atResistance = r.DayRangeSignal == "Near High" || r.VwapBandSignal == "At Upper Band" || r.MarketProfileSignal.Contains("VAH");
            bool volumeConfirmed = r.VolumeSignal == "Volume Burst";
            bool isNotInStrongTrend = !r.MarketThesis.ToString().Contains("Trend");

            var fiveMinCandles = _stateManager.GetCandles(r.SecurityId, TimeSpan.FromMinutes(5));
            Candle? lastFiveMinCandle = fiveMinCandles?.LastOrDefault();
            bool isBullishCandle = lastFiveMinCandle != null && lastFiveMinCandle.Close > lastFiveMinCandle.Open;
            bool isBearishCandle = lastFiveMinCandle != null && lastFiveMinCandle.Close < lastFiveMinCandle.Open;

            switch (driverName)
            {
                case "Bullish Pattern at Key Support": return isBullishPattern && atSupport;
                case "Bearish Pattern at Key Resistance": return isBearishPattern && atResistance;
                case "Bullish Pattern with Volume Confirmation": return isBullishPattern && volumeConfirmed;
                case "Bearish Pattern with Volume Confirmation": return isBearishPattern && volumeConfirmed;
                case "Bullish Pattern (Standalone)": return isBullishPattern && !atSupport && !volumeConfirmed;
                case "Bearish Pattern (Standalone)": return isBearishPattern && !atResistance && !volumeConfirmed;
                case "Aggressive Buying Pressure": return r.MicroFlowSignal == "Aggressive Buying";
                case "Aggressive Selling Pressure": return r.MicroFlowSignal == "Aggressive Selling";
                case "Institutional Intent is Bullish": return r.InstitutionalIntent.Contains("Bullish");
                case "Institutional Intent is Bearish": return r.InstitutionalIntent.Contains("Bearish");
                case "Price above VWAP": return r.PriceVsVwapSignal == "Above VWAP";
                case "Price below VWAP": return r.PriceVsVwapSignal == "Below VWAP";
                case "5m EMA confirms bullish trend": return r.EmaSignal5Min == "Bullish Cross";
                case "5m EMA confirms bearish trend": return r.EmaSignal5Min == "Bearish Cross";
                case "Bullish Trend Continuation":
                    return r.ActiveThesis == "Bullish_Trend_Continuation" && r.LTP > r.ActiveThesisEntryPrice && r.PriceVsVwapSignal == "Above VWAP";
                case "Bearish Trend Continuation":
                    return r.ActiveThesis == "Bearish_Trend_Continuation" && r.LTP < r.ActiveThesisEntryPrice && r.PriceVsVwapSignal == "Below VWAP";
                case "OI confirms new longs": return r.OiSignal == "Long Buildup";
                case "OI confirms new shorts": return r.OiSignal == "Short Buildup";
                case "High OTM Call Gamma": return r.GammaSignal == "High OTM Call Gamma";
                case "High OTM Put Gamma": return r.GammaSignal == "High OTM Put Gamma";
                case "Bullish Skew Divergence (Full)": return r.IvSkewSignal.Contains("Bullish") && !isNotInStrongTrend;
                case "Bearish Skew Divergence (Full)": return r.IvSkewSignal.Contains("Bearish") && !isNotInStrongTrend;
                case "True Acceptance Above Y-VAH": return r.MarketProfileSignal == "True Acceptance Above Y-VAH";
                case "True Acceptance Below Y-VAL": return r.MarketProfileSignal == "True Acceptance Below Y-VAL";
                case "Look Above and Fail at Y-VAH": return r.MarketProfileSignal == "Look Above and Fail at Y-VAH";
                case "Look Below and Fail at Y-VAL": return r.MarketProfileSignal == "Look Below and Fail at Y-VAL";
                case "IB breakout is extending": return r.InitialBalanceSignal == "IB Extension Up";
                case "IB breakdown is extending": return r.InitialBalanceSignal == "IB Extension Down";
                case "Bullish Breakout on Volume Burst": return volumeConfirmed && isBullishCandle;
                case "Bearish Breakdown on Volume Burst": return volumeConfirmed && isBearishCandle;
                case "Option Breakout Setup": return r.VolatilityStateSignal == "IV Squeeze Setup";
                case "Range Contraction": return r.AtrSignal5Min == "Vol Contracting";
                case "Bullish OBV Div at range low": return r.ObvDivergenceSignal5Min.Contains("Bullish") && atSupport && !isNotInStrongTrend;
                case "Bearish OBV Div at range high": return r.ObvDivergenceSignal5Min.Contains("Bearish") && atResistance && !isNotInStrongTrend;
                case "Bullish RSI Div at range low": return r.RsiSignal5Min.Contains("Bullish") && atSupport && !isNotInStrongTrend;
                case "Bearish RSI Div at range high": return r.RsiSignal5Min.Contains("Bearish") && atResistance && !isNotInStrongTrend;
                case "Low volume suggests exhaustion (Bullish)": return !volumeConfirmed && r.AtrSignal5Min == "Vol Contracting" && r.DayRangeSignal == "Near Low";
                case "Low volume suggests exhaustion (Bearish)": return !volumeConfirmed && r.AtrSignal5Min == "Vol Contracting" && r.DayRangeSignal == "Near High";
                case "Price Above GEX Flip Point (Bullish Hedging Flow)": return r.GexFlipPoint > 0 && r.LTP > r.GexFlipPoint;
                case "Price Below GEX Flip Point (Bearish Hedging Flow)": return r.GexFlipPoint > 0 && r.LTP < r.GexFlipPoint;
                case "Net GEX is Negative (Market Makers are Short Gamma, Volatility Amplified)": return r.NetGex < 0;
                case "Net GEX is Positive (Volatility Dampened)": return r.NetGex > 0;
                case "Price Approaching Max GEX Level (Pinning Risk)": return r.MaxGexLevel > 0 && Math.Abs(r.LTP - r.MaxGexLevel) < (r.LTP * 0.001m);
                default: return false;
            }
        }

        // --- Preserved Helper Methods from Your Original File ---
        private void UpdateIntradayThesis(AnalysisResult result)
        {
            result.DominantPlayer = DetermineDominantPlayer(result);
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

        private string GenerateMarketNarrative(AnalysisResult r)
        {
            return $"Playbook: {r.FinalTradeSignal}. Drivers: {r.BullishDrivers.Count} bullish vs. {r.BearishDrivers.Count} bearish. Confidence: {r.ConvictionScore}.";
        }
    }
}