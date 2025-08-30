// TradingConsole.Wpf/Services/Analysis/SignalGenerationService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TradingConsole.Core.Models;
using TradingConsole.Wpf.Services.Analysis;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Wpf.Services
{
    public class SignalGenerationService
    {
        private readonly AnalysisStateManager _stateManager;
        private readonly SettingsViewModel _settingsViewModel;
        private readonly HistoricalIvService _historicalIvService;
        private readonly IndicatorService _indicatorService;
        private readonly MicroFlowService _microFlowService;

        public SignalGenerationService(AnalysisStateManager stateManager, SettingsViewModel settingsViewModel, HistoricalIvService historicalIvService, IndicatorService indicatorService, MicroFlowService microFlowService)
        {
            _stateManager = stateManager;
            _settingsViewModel = settingsViewModel;
            _historicalIvService = historicalIvService;
            _indicatorService = indicatorService;
            _microFlowService = microFlowService;
        }

        public void GenerateAllSignals(DashboardInstrument instrument, DashboardInstrument instrumentForAnalysis, AnalysisResult result, System.Collections.ObjectModel.ObservableCollection<OptionChainRow> optionChain)
        {
            var tickState = _stateManager.TickAnalysisState[instrumentForAnalysis.SecurityId];
            tickState.cumulativePriceVolume += instrumentForAnalysis.AvgTradePrice * instrumentForAnalysis.LastTradedQuantity;
            tickState.cumulativeVolume += instrumentForAnalysis.LastTradedQuantity;
            result.Vwap = (tickState.cumulativeVolume > 0) ? tickState.cumulativePriceVolume / tickState.cumulativeVolume : 0;
            if (instrument.ImpliedVolatility > 0)
            {
                var ivHistory = _stateManager.TickAnalysisState[instrumentForAnalysis.SecurityId].ivHistory;
                ivHistory.Add(instrument.ImpliedVolatility);
                if (ivHistory.Count > _settingsViewModel.IvHistoryLength)
                {
                    ivHistory.RemoveAt(0);
                }
                var (avgIv, ivSignal) = CalculateIvSignal(instrument.ImpliedVolatility, ivHistory);
                result.CurrentIv = instrument.ImpliedVolatility;
                result.AvgIv = avgIv;
                result.IvSignal = ivSignal;
            }

            _stateManager.TickAnalysisState[instrumentForAnalysis.SecurityId] = tickState;
            _microFlowService.AddTick(instrumentForAnalysis.SecurityId, instrumentForAnalysis.LTP, instrumentForAnalysis.LastTradedQuantity);
            result.MicroFlowSignal = _microFlowService.AnalyzeMicroFlow(instrumentForAnalysis.SecurityId);


            var (priceVsVwap, priceVsClose, dayRange) = CalculatePriceActionSignals(instrument, result.Vwap);
            result.PriceVsVwapSignal = priceVsVwap;
            result.PriceVsCloseSignal = priceVsClose;
            result.DayRangeSignal = dayRange;

            var threeMinCandles = _stateManager.GetCandles(instrumentForAnalysis.SecurityId, TimeSpan.FromMinutes(3));
            if (threeMinCandles != null && threeMinCandles.Any())
            {
                result.OiSignal = CalculateOiSignal(threeMinCandles);
            }

            var oneMinCandles = _stateManager.GetCandles(instrumentForAnalysis.SecurityId, TimeSpan.FromMinutes(1));
            if (oneMinCandles != null && oneMinCandles.Any())
            {
                var (volSignal, currentVol, avgVol) = CalculateVolumeSignal(oneMinCandles);
                result.VolumeSignal = volSignal;
                result.CurrentVolume = currentVol;
                result.AvgVolume = avgVol;
                result.OpenTypeSignal = AnalyzeOpenType(instrument, oneMinCandles);
                var (vwapBandSignal, upperBand, lowerBand) = CalculateVwapBandSignal(instrument.LTP, oneMinCandles);
                result.VwapBandSignal = vwapBandSignal;
                result.VwapUpperBand = upperBand;
                result.VwapLowerBand = lowerBand;
                result.AnchoredVwap = CalculateAnchoredVwap(oneMinCandles);
            }

            var fiveMinCandles = _stateManager.GetCandles(instrumentForAnalysis.SecurityId, TimeSpan.FromMinutes(5));
            var fifteenMinCandles = _stateManager.GetCandles(instrumentForAnalysis.SecurityId, TimeSpan.FromMinutes(15));

            if (oneMinCandles != null && oneMinCandles.Any())
            {
                var priceState1m = _stateManager.MultiTimeframePriceEmaState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(1)];
                result.EmaSignal1Min = _indicatorService.CalculateEmaSignal(oneMinCandles, priceState1m, _settingsViewModel.ShortEmaLength, _settingsViewModel.LongEmaLength, false, instrumentForAnalysis);

                var vwapState1m = _stateManager.MultiTimeframeVwapEmaState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(1)];
                result.VwapEmaSignal1Min = _indicatorService.CalculateEmaSignal(oneMinCandles, vwapState1m, _settingsViewModel.ShortEmaLength, _settingsViewModel.LongEmaLength, true, instrumentForAnalysis);
            }
            if (fiveMinCandles != null && fiveMinCandles.Any())
            {
                var priceState5m = _stateManager.MultiTimeframePriceEmaState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(5)];
                result.EmaSignal5Min = _indicatorService.CalculateEmaSignal(fiveMinCandles, priceState5m, _settingsViewModel.ShortEmaLength, _settingsViewModel.LongEmaLength, false, instrumentForAnalysis);

                var vwapState5m = _stateManager.MultiTimeframeVwapEmaState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(5)];
                result.VwapEmaSignal5Min = _indicatorService.CalculateEmaSignal(fiveMinCandles, vwapState5m, _settingsViewModel.ShortEmaLength, _settingsViewModel.LongEmaLength, true, instrumentForAnalysis);
            }
            if (fifteenMinCandles != null && fifteenMinCandles.Any())
            {
                var priceState15m = _stateManager.MultiTimeframePriceEmaState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(15)];
                result.EmaSignal15Min = _indicatorService.CalculateEmaSignal(fifteenMinCandles, priceState15m, _settingsViewModel.ShortEmaLength, _settingsViewModel.LongEmaLength, false, instrumentForAnalysis);

                var vwapState15m = _stateManager.MultiTimeframeVwapEmaState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(15)];
                result.VwapEmaSignal15Min = _indicatorService.CalculateEmaSignal(fifteenMinCandles, vwapState15m, _settingsViewModel.ShortEmaLength, _settingsViewModel.LongEmaLength, true, instrumentForAnalysis);
            }

            if (oneMinCandles != null && oneMinCandles.Any())
            {
                var rsiState = _stateManager.MultiTimeframeRsiState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(1)];
                result.RsiValue1Min = _indicatorService.CalculateRsi(oneMinCandles, rsiState, _settingsViewModel.RsiPeriod);
                result.RsiSignal1Min = _indicatorService.DetectRsiDivergence(oneMinCandles, rsiState, _settingsViewModel.RsiDivergenceLookback);
            }
            if (fiveMinCandles != null && fiveMinCandles.Any())
            {
                var rsiState = _stateManager.MultiTimeframeRsiState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(5)];
                result.RsiValue5Min = _indicatorService.CalculateRsi(fiveMinCandles, rsiState, _settingsViewModel.RsiPeriod);
                result.RsiSignal5Min = _indicatorService.DetectRsiDivergence(fiveMinCandles, rsiState, _settingsViewModel.RsiDivergenceLookback);
            }

            if (oneMinCandles != null && oneMinCandles.Any())
            {
                var atrState = _stateManager.MultiTimeframeAtrState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(1)];
                result.Atr1Min = _indicatorService.CalculateAtr(oneMinCandles, atrState, _settingsViewModel.AtrPeriod);

                var obvState = _stateManager.MultiTimeframeObvState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(1)];
                result.ObvValue1Min = _indicatorService.CalculateObv(oneMinCandles, obvState);

                result.ObvDivergenceSignal1Min = _indicatorService.DetectObvDivergence(oneMinCandles, obvState, _settingsViewModel.RsiDivergenceLookback);
            }
            if (fiveMinCandles != null && fiveMinCandles.Any())
            {
                var atrState = _stateManager.MultiTimeframeAtrState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(5)];
                result.Atr5Min = _indicatorService.CalculateAtr(fiveMinCandles, atrState, _settingsViewModel.AtrPeriod);
                result.AtrSignal5Min = (atrState.AtrValues.Count > 2 && result.Atr5Min < atrState.AtrValues[^2]) ? "Vol Contracting" : "Vol Expanding";

                var obvState = _stateManager.MultiTimeframeObvState[instrumentForAnalysis.SecurityId][TimeSpan.FromMinutes(5)];
                result.ObvValue5Min = _indicatorService.CalculateObv(fiveMinCandles, obvState);

                result.ObvDivergenceSignal5Min = _indicatorService.DetectObvDivergence(fiveMinCandles, obvState, _settingsViewModel.RsiDivergenceLookback);
            }

            if (oneMinCandles != null) result.CandleSignal1Min = RecognizeCandlestickPattern(oneMinCandles, result);
            if (fiveMinCandles != null)
            {
                result.CandleSignal5Min = RecognizeCandlestickPattern(fiveMinCandles, result);
                result.MarketRegime = CalculateMarketRegime(fiveMinCandles, instrumentForAnalysis.SecurityId);
            }

            if (_stateManager.MarketProfiles.TryGetValue(instrument.SecurityId, out var liveProfile))
            {
                result.InitialBalanceSignal = GetInitialBalanceSignal(instrument.LTP, liveProfile, instrument.SecurityId);
                result.InitialBalanceHigh = liveProfile.InitialBalanceHigh;
                result.InitialBalanceLow = liveProfile.InitialBalanceLow;
                result.DevelopingPoc = liveProfile.DevelopingTpoLevels.PointOfControl;
                result.DevelopingVah = liveProfile.DevelopingTpoLevels.ValueAreaHigh;
                result.DevelopingVal = liveProfile.DevelopingTpoLevels.ValueAreaLow;
                result.DevelopingVpoc = liveProfile.DevelopingVolumeProfile.VolumePoc;
                RunMarketProfileAnalysis(instrument, liveProfile, result, oneMinCandles);
            }
            var yesterdayProfile = _stateManager.HistoricalMarketProfiles.GetValueOrDefault(instrument.SecurityId)?.FirstOrDefault(p => p.Date.Date < DateTime.Today);
            result.YesterdayProfileSignal = AnalyzePriceRelativeToYesterdayProfile(instrument.LTP, yesterdayProfile);
            if (instrument.InstrumentType == "INDEX") { result.InstitutionalIntent = RunTier1InstitutionalIntentAnalysis(instrument); }
            result.VolatilityStateSignal = GenerateVolatilityStateSignal(instrumentForAnalysis, result);

            result.IntradayIvSpikeSignal = CalculateIntradayIvSpikeSignal(instrument);
            result.GammaSignal = CalculateGammaSignal(instrument, result.LTP, optionChain);

            if (instrument.InstrumentType == "INDEX")
            {
                result.IvSkewSignal = CalculateIvSkewSignal(instrument, optionChain, result);
            }
        }

        #region Signal Calculation Logic

        private (decimal avgIv, string ivSignal) CalculateIvSignal(decimal currentIv, List<decimal> ivHistory)
        {
            string signal = "Neutral";
            decimal avgIv = 0;
            var validIvHistory = ivHistory.Where(iv => iv > 0).ToList();

            if (validIvHistory.Any() && validIvHistory.Count >= _settingsViewModel.IvHistoryLength)
            {
                avgIv = validIvHistory.Average();
                if (currentIv > (avgIv + _settingsViewModel.IvSpikeThreshold))
                {
                    signal = "IV Spike Up";
                }
                else if (currentIv < (avgIv - _settingsViewModel.IvSpikeThreshold))
                {
                    signal = "IV Drop Down";
                }
            }
            else if (currentIv > 0)
            {
                signal = "Building History...";
            }
            return (avgIv, signal);
        }

        private string CalculateGammaSignal(DashboardInstrument instrument, decimal underlyingPrice, System.Collections.ObjectModel.ObservableCollection<OptionChainRow> optionChain)
        {
            if (instrument.InstrumentType != "INDEX" || optionChain == null || !optionChain.Any())
            {
                return "N/A";
            }

            var sortedStrikes = optionChain.OrderBy(r => r.StrikePrice).ToList();
            var atmStrike = sortedStrikes.OrderBy(r => Math.Abs(r.StrikePrice - underlyingPrice)).FirstOrDefault();
            if (atmStrike == null) return "N/A";

            int atmIndex = sortedStrikes.IndexOf(atmStrike);

            const int otmCount = 4;
            var otmCallStrikes = sortedStrikes.Skip(atmIndex + 1).Take(otmCount).ToList();
            var otmPutStrikes = sortedStrikes.Take(atmIndex).Reverse().Take(otmCount).ToList();

            if (otmCallStrikes.Count < otmCount || otmPutStrikes.Count < otmCount)
            {
                return "Insufficient OTM Strikes";
            }

            decimal totalOtmCallGamma = otmCallStrikes.Sum(s => s.CallOption?.Gamma ?? 0);
            decimal totalOtmPutGamma = otmPutStrikes.Sum(s => s.PutOption?.Gamma ?? 0);

            decimal userRatio = _settingsViewModel.GammaImbalanceRatio;
            if (userRatio <= 1) userRatio = 3;
            decimal internalRatio = (userRatio - 1) / (userRatio + 1);

            decimal difference = totalOtmCallGamma - totalOtmPutGamma;
            decimal totalGamma = totalOtmCallGamma + totalOtmPutGamma;
            decimal calculatedRatio = totalGamma > 0 ? Math.Abs(difference) / totalGamma : 0;

            if (calculatedRatio > internalRatio)
            {
                if (totalOtmCallGamma > totalOtmPutGamma)
                {
                    return "High OTM Call Gamma";
                }
                else
                {
                    return "High OTM Put Gamma";
                }
            }

            if (totalOtmCallGamma > _settingsViewModel.AtmGammaThreshold && totalOtmPutGamma > _settingsViewModel.AtmGammaThreshold)
            {
                return "Balanced OTM Gamma";
            }

            return "Neutral";
        }

        private string CalculateIvSkewSignal(DashboardInstrument instrument, System.Collections.ObjectModel.ObservableCollection<OptionChainRow> optionChain, AnalysisResult result)
        {
            if (optionChain == null || !optionChain.Any())
            {
                Debug.WriteLine("[IVSkew] N/A - Option chain is empty.");
                return "N/A";
            }

            var atmStrikeRow = optionChain.OrderBy(r => Math.Abs(r.StrikePrice - instrument.LTP)).FirstOrDefault();
            if (atmStrikeRow == null) return "N/A";

            var otmCall = optionChain.Where(r => r.StrikePrice > atmStrikeRow.StrikePrice && r.CallOption.IV > 0).OrderBy(r => r.StrikePrice).FirstOrDefault();
            var otmPut = optionChain.Where(r => r.StrikePrice < atmStrikeRow.StrikePrice && r.PutOption.IV > 0).OrderByDescending(r => r.StrikePrice).FirstOrDefault();

            if (otmCall == null || otmPut == null || otmCall.CallOption.IV <= 0 || otmPut.PutOption.IV <= 0)
            {
                return "N/A";
            }

            // 1. Calculate the ratio
            result.SkewRatio = otmPut.PutOption.IV / otmCall.CallOption.IV;

            // 2. Store history
            var history = _stateManager.SkewRatioHistory[instrument.SecurityId];
            history.Add(result.SkewRatio);
            if (history.Count > 20) history.RemoveAt(0); // Keep a 20-period history

            // 3. Generate signal based on the ratio's behavior
            if (history.Count > 10)
            {
                var movingAverage = history.Average();
                bool isPriceTrendingUp = instrument.LTP > instrument.Open;

                // Divergence: Price is up, but fear (skew ratio) is also rising above its average
                if (isPriceTrendingUp && result.SkewRatio > movingAverage * 1.05m)
                {
                    return "Bearish Divergence (Price Up, Fear Up)";
                }
            }

            return "Neutral Skew";
        }

        public void UpdateIvMetrics(DashboardInstrument instrument, decimal underlyingPrice)
        {
            if (!instrument.InstrumentType.StartsWith("OPT") || instrument.ImpliedVolatility <= 0) return;

            var ivKey = GetHistoricalIvKey(instrument, underlyingPrice);
            if (string.IsNullOrEmpty(ivKey)) return;

            // --- THE FIX: Logic to trigger the snapshot recording at the correct time ---
            var istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
            var istNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istZone);

            // Only attempt to record the IV snapshot after 10:30 AM IST.
            // The logic in HistoricalIvService ensures it only happens once per day.
            if (istNow.TimeOfDay >= new TimeSpan(10, 30, 0))
            {
                _historicalIvService.RecordIvSnapshot(ivKey, instrument.ImpliedVolatility);
            }

            var (ivRank, ivPercentile) = CalculateIvRankAndPercentile(instrument.ImpliedVolatility, ivKey);

            var underlyingInstrument = _stateManager.AnalysisResults.Values.FirstOrDefault(r => r.Symbol == instrument.UnderlyingSymbol);
            if (underlyingInstrument != null)
            {
                var result = _stateManager.GetResult(underlyingInstrument.SecurityId);
                result.IvRank = ivRank;
                result.IvPercentile = ivPercentile; // Also update percentile
            }
        }

        private void RunMarketProfileAnalysis(DashboardInstrument instrument, MarketProfile currentProfile, AnalysisResult result, List<Candle>? oneMinCandles)
        {
            var previousDayProfile = _stateManager.HistoricalMarketProfiles.GetValueOrDefault(instrument.SecurityId)?.FirstOrDefault(p => p.Date.Date < DateTime.Today.Date);
            if (previousDayProfile == null)
            {
                result.MarketProfileSignal = "Awaiting Previous Day Data";
                return;
            }

            var ltp = instrument.LTP;
            var prevVAH = previousDayProfile.TpoLevelsInfo.ValueAreaHigh;
            var prevVAL = previousDayProfile.TpoLevelsInfo.ValueAreaLow;
            var currentVAH = currentProfile.DevelopingTpoLevels.ValueAreaHigh;
            var currentVAL = currentProfile.DevelopingTpoLevels.ValueAreaLow;

            if (currentVAL > prevVAH) { result.MarketProfileSignal = "True Acceptance Above Y-VAH"; return; }
            if (currentVAH < prevVAL) { result.MarketProfileSignal = "True Acceptance Below Y-VAL"; return; }

            if (oneMinCandles != null && oneMinCandles.Count > 2)
            {
                var lastCandle = oneMinCandles.Last();
                var secondLastCandle = oneMinCandles[^2];

                if (secondLastCandle.High > prevVAH && lastCandle.Close < prevVAH) { result.MarketProfileSignal = "Look Above and Fail at Y-VAH"; return; }
                if (secondLastCandle.Low < prevVAL && lastCandle.Close > prevVAL) { result.MarketProfileSignal = "Look Below and Fail at Y-VAL"; return; }
            }

            if (ltp > prevVAH) { result.MarketProfileSignal = "Initiative Buying Above Y-VAH"; return; }
            if (ltp < prevVAL) { result.MarketProfileSignal = "Initiative Selling Below Y-VAL"; return; }

            result.MarketProfileSignal = "Trading Inside Y-Value";
        }

        private string CalculateOiSignal(List<Candle> candles)
        {
            const int lookbackPeriod = 5;
            if (candles.Count < lookbackPeriod) return "Building History...";

            var relevantCandles = candles.TakeLast(lookbackPeriod).ToList();
            var firstCandle = relevantCandles.First();
            var lastCandle = relevantCandles.Last();

            if (firstCandle.OpenInterest == 0 || lastCandle.OpenInterest == 0) return "Building History...";

            decimal priceChange = lastCandle.Close - firstCandle.Open;
            long oiChange = lastCandle.OpenInterest - firstCandle.OpenInterest;

            bool isPriceUp = priceChange > 0;
            bool isPriceDown = priceChange < 0;
            bool isOiUp = oiChange > 0;
            bool isOiDown = oiChange < 0;

            if (isPriceUp && isOiUp) return "Long Buildup";
            if (isPriceUp && isOiDown) return "Short Covering";
            if (isPriceDown && isOiUp) return "Short Buildup";
            if (isPriceDown && isOiDown) return "Long Unwinding";

            return "Neutral";
        }

        private string CalculateMarketRegime(List<Candle> fiveMinCandles, string securityId)
        {
            var atrState = _stateManager.MultiTimeframeAtrState[securityId][TimeSpan.FromMinutes(5)];
            if (atrState.AtrValues.Count < 10) return "Calculating...";

            decimal currentAtr = atrState.CurrentAtr;
            decimal avgAtr = atrState.AtrValues.TakeLast(10).Average();

            if (currentAtr > avgAtr * 1.5m) return "High Volatility";
            if (currentAtr < avgAtr * 0.7m) return "Low Volatility";
            return "Normal Volatility";
        }

        private string CalculateIntradayIvSpikeSignal(DashboardInstrument instrument)
        {
            var ivKey = GetHistoricalIvKey(instrument, 0);
            if (!_stateManager.IntradayIvStates.TryGetValue(ivKey, out var ivState) || ivState.IvHistory.Count < 10)
            {
                return "N/A";
            }

            decimal currentIv = instrument.ImpliedVolatility;
            decimal avgIv = ivState.IvHistory.Average();
            if (avgIv == 0) return "N/A";
            decimal ivChange = (currentIv - avgIv) / avgIv;

            if (ivChange > _settingsViewModel.IvSpikeThreshold) return "IV Spike Up";
            if (ivChange < -_settingsViewModel.IvSpikeThreshold) return "IV Spike Down";
            return "IV Stable";
        }

        public void UpdateMarketProfile(MarketProfile profile, Candle priceCandle, Candle volumeCandle)
        {
            profile.UpdateInitialBalance(priceCandle);
            var tpoPeriod = profile.GetTpoPeriod(priceCandle.Timestamp);

            var priceRange = new List<decimal>();
            for (decimal price = priceCandle.Low; price <= priceCandle.High; price += profile.TickSize)
            {
                priceRange.Add(profile.QuantizePrice(price));
            }

            if (priceRange.Any())
            {
                long volumePerTick = priceRange.Count > 0 ? volumeCandle.Volume / priceRange.Count : 0;
                foreach (var price in priceRange)
                {
                    if (!profile.TpoLevels.ContainsKey(price)) profile.TpoLevels[price] = new List<char>();
                    if (!profile.TpoLevels[price].Contains(tpoPeriod)) profile.TpoLevels[price].Add(tpoPeriod);

                    if (!profile.VolumeLevels.ContainsKey(price)) profile.VolumeLevels[price] = 0;
                    profile.VolumeLevels[price] += volumePerTick;
                }
            }
            profile.CalculateProfileMetrics();
        }

        private (string, long, long) CalculateVolumeSignal(List<Candle> candles) { if (!candles.Any()) return ("N/A", 0, 0); long currentCandleVolume = candles.Last().Volume; if (candles.Count < 2) return ("Building History...", currentCandleVolume, 0); var historyCandles = candles.Take(candles.Count - 1).ToList(); if (historyCandles.Count > _settingsViewModel.VolumeHistoryLength) { historyCandles = historyCandles.Skip(historyCandles.Count - _settingsViewModel.VolumeHistoryLength).ToList(); } if (!historyCandles.Any()) return ("Building History...", currentCandleVolume, 0); double averageVolume = historyCandles.Average(c => (double)c.Volume); if (averageVolume > 0 && currentCandleVolume > (averageVolume * _settingsViewModel.VolumeBurstMultiplier)) { return ("Volume Burst", currentCandleVolume, (long)averageVolume); } return ("Neutral", currentCandleVolume, (long)averageVolume); }
        private (string priceVsVwap, string priceVsClose, string dayRange) CalculatePriceActionSignals(DashboardInstrument instrument, decimal vwap) { string priceVsVwap = (vwap > 0) ? (instrument.LTP > vwap ? "Above VWAP" : "Below VWAP") : "Neutral"; string priceVsClose = (instrument.Close > 0) ? (instrument.LTP > instrument.Close ? "Above Close" : "Below Close") : "Neutral"; string dayRange = "Mid-Range"; decimal range = instrument.High - instrument.Low; if (range > 0) { decimal position = (instrument.LTP - instrument.Low) / range; if (position > 0.8m) dayRange = "Near High"; else if (position < 0.2m) dayRange = "Near Low"; } return (priceVsVwap, priceVsClose, dayRange); }

        private string RecognizeCandlestickPattern(List<Candle> candles, AnalysisResult analysisResult)
        {
            if (candles.Count < 3) return "N/A";

            string securityId = analysisResult.SecurityId;
            var lastCandle = candles.Last();

            // Check if a previously confirmed pattern has been invalidated
            if (_stateManager.ActiveCandlestickPatterns.TryGetValue(securityId, out var activePattern))
            {
                bool isInvalidated = false;
                if (activePattern.IsBullish)
                {
                    if (lastCandle.Close < activePattern.InvalidationLevel)
                    {
                        isInvalidated = true;
                    }
                }
                else // Bearish pattern
                {
                    if (lastCandle.Close > activePattern.InvalidationLevel)
                    {
                        isInvalidated = true;
                    }
                }

                if (isInvalidated)
                {
                    _stateManager.ActiveCandlestickPatterns.TryRemove(securityId, out _);
                    return $"{activePattern.PatternName} (Invalidated)";
                }
            }

            // Identify a new pattern on the most recently closed candle
            var patternCandle = candles.Last();
            var priorCandle = candles[^2];
            var oldestCandle = candles[^3];
            string newPattern = IdentifySingleCandlePattern(patternCandle, priorCandle, oldestCandle);

            if (newPattern != "N/A" && newPattern != "Neutral Doji")
            {
                // A new, valid pattern has formed. Confirm it and set the invalidation level.
                decimal confirmationCandleBody = Math.Abs(patternCandle.Close - patternCandle.Open);
                decimal invalidationLevel;
                bool isBullish = newPattern.Contains("Bullish");

                if (isBullish)
                {
                    invalidationLevel = patternCandle.Open + (confirmationCandleBody / 2);
                }
                else // Bearish
                {
                    invalidationLevel = patternCandle.Open - (confirmationCandleBody / 2);
                }

                var newActivePattern = new ActivePatternState
                {
                    PatternName = newPattern,
                    ConfirmationCandleTimestamp = patternCandle.Timestamp,
                    InvalidationLevel = invalidationLevel,
                    IsBullish = isBullish
                };

                _stateManager.ActiveCandlestickPatterns[securityId] = newActivePattern;

                string context = GetPatternContext(analysisResult);
                string volumeInfo = GetVolumeConfirmation(patternCandle, priorCandle);

                return $"Confirmed {newPattern}{context}{volumeInfo}";
            }

            // If no new pattern and an old one is still valid, show the old one.
            if (_stateManager.ActiveCandlestickPatterns.TryGetValue(securityId, out var existingPattern))
            {
                return $"Active: {existingPattern.PatternName}";
            }

            return "N/A";
        }

        private string IdentifySingleCandlePattern(Candle c1, Candle c2, Candle c3)
        {
            // --- Properties of the most recent candle (c1) ---
            decimal body1 = Math.Abs(c1.Open - c1.Close);
            decimal range1 = c1.High - c1.Low;
            if (range1 == 0) return "N/A";
            bool isC1Bullish = c1.Close > c1.Open;
            bool isC1Bearish = c1.Close < c1.Open;

            // --- Properties of the middle candle (c2) ---
            decimal body2 = Math.Abs(c2.Open - c2.Close);
            decimal range2 = c2.High - c2.Low;
            if (range2 == 0) return "N/A";
            bool isC2Bullish = c2.Close > c2.Open;
            bool isC2Bearish = c2.Close < c2.Open;

            // --- Properties of the oldest candle (c3) ---
            decimal body3 = Math.Abs(c3.Open - c3.Close);
            decimal range3 = c3.High - c3.Low;
            if (range3 == 0) return "N/A";
            bool isC3Bullish = c3.Close > c3.Open;
            bool isC3Bearish = c3.Close < c3.Open;

            // =============================================
            // THREE-CANDLE PATTERNS (Highest Priority)
            // =============================================

            // Morning Star (Bullish Reversal)
            if (isC3Bearish && body3 > range2 * 1.5m && // Strong bearish candle
                body2 < body3 * 0.6m &&              // Small middle candle (Doji/Spinning Top)
                c2.Close < c3.Close && c2.Open < c3.Close && // It gapped down
                isC1Bullish && c1.Close > (c3.Open + c3.Close) / 2)
            {
                return "Bullish Morning Star";
            }

            // Evening Star (Bearish Reversal)
            if (isC3Bullish && body3 > range2 * 1.5m && // Strong bullish candle
                body2 < body3 * 0.6m &&              // Small middle candle
                c2.Close > c3.Close && c2.Open > c3.Close && // It gapped up
                isC1Bearish && c1.Close < (c3.Open + c3.Close) / 2)
            {
                return "Bearish Evening Star";
            }

            // Three White Soldiers (Bullish)
            if (isC1Bullish && isC2Bullish && isC3Bullish &&
                c1.Close > c2.Close && c2.Close > c3.Close && // Higher closes
                c1.Open > c2.Open && c1.Open < c2.Close &&   // Opens within the previous body
                c2.Open > c3.Open && c2.Open < c3.Close &&
                (body1 / range1 > 0.7m) && (body2 / range2 > 0.7m) && (body3 / range3 > 0.7m)) // Strong bodies
            {
                return "Three White Soldiers";
            }

            // Three Black Crows (Bearish)
            if (isC1Bearish && isC2Bearish && isC3Bearish &&
                c1.Close < c2.Close && c2.Close < c3.Close && // Lower closes
                c1.Open < c2.Open && c1.Open > c2.Close &&   // Opens within the previous body
                c2.Open < c3.Open && c2.Open > c3.Close &&
                (body1 / range1 > 0.7m) && (body2 / range2 > 0.7m) && (body3 / range3 > 0.7m)) // Strong bodies
            {
                return "Three Black Crows";
            }

            // Three Inside Up (Bullish Reversal)
            // c3 is bearish, c2 is a bullish harami, c1 confirms by closing higher.
            if (isC3Bearish && isC2Bullish && body2 < body3 && c2.Close < c3.Open && c2.Open > c3.Close &&
                isC1Bullish && c1.Close > c3.Open)
            {
                return "Three Inside Up";
            }

            // Three Inside Down (Bearish Reversal)
            // c3 is bullish, c2 is a bearish harami, c1 confirms by closing lower.
            if (isC3Bullish && isC2Bearish && body2 < body3 && c2.Open < c3.Close && c2.Close > c3.Open &&
                isC1Bearish && c1.Close < c3.Open)
            {
                return "Three Inside Down";
            }

            // Three Outside Up (Bullish Reversal)
            // c2 & c3 form a bullish engulfing, c1 confirms with a higher close.
            if (isC3Bearish && isC2Bullish && c2.Close > c3.Open && c2.Open < c3.Close &&
                isC1Bullish && c1.Close > c2.Close)
            {
                return "Three Outside Up";
            }

            // Three Outside Down (Bearish Reversal)
            // c2 & c3 form a bearish engulfing, c1 confirms with a lower close.
            if (isC3Bullish && isC2Bearish && c2.Open > c3.Close && c2.Close < c3.Open &&
                isC1Bearish && c1.Close < c2.Close)
            {
                return "Three Outside Down";
            }


            // =============================================
            // TWO-CANDLE PATTERNS
            // =============================================

            // Using c2 and c1 for two-candle patterns

            // Bullish Engulfing
            if (isC2Bearish && isC1Bullish && c1.Close > c2.Open && c1.Open < c2.Close)
            {
                return "Bullish Engulfing";
            }

            // Bearish Engulfing
            if (isC2Bullish && isC1Bearish && c1.Open > c2.Close && c1.Close < c2.Open)
            {
                return "Bearish Engulfing";
            }

            // Bullish Harami
            if (isC2Bearish && isC1Bullish && body1 < body2 && c1.Close < c2.Open && c1.Open > c2.Close)
            {
                return "Bullish Harami";
            }

            // Bearish Harami
            if (isC2Bullish && isC1Bearish && body1 < body2 && c1.Open < c2.Close && c1.Close > c2.Open)
            {
                return "Bearish Harami";
            }

            // Piercing Line (Bullish)
            if (isC2Bearish && isC1Bullish && c1.Open < c2.Low && c1.Close > (c2.Open + c2.Close) / 2 && c1.Close < c2.Open)
            {
                return "Piercing Line";
            }

            // Dark Cloud Cover (Bearish)
            if (isC2Bullish && isC1Bearish && c1.Open > c2.High && c1.Close < (c2.Open + c2.Close) / 2 && c1.Close > c2.Open)
            {
                return "Dark Cloud Cover";
            }


            // =============================================
            // SINGLE-CANDLE PATTERNS
            // =============================================

            // Using only c1 for single-candle patterns
            decimal upperShadow1 = c1.High - Math.Max(c1.Open, c1.Close);
            decimal lowerShadow1 = Math.Min(c1.Open, c1.Close) - c1.Low;

            // Doji (very small body)
            if (body1 / range1 < 0.05m)
            {
                return "Neutral Doji";
            }

            // Hammer (small body, long lower wick) - typically after a downtrend
            if (lowerShadow1 > body1 * 2 && upperShadow1 < body1 * 0.5m)
            {
                return "Bullish Hammer";
            }

            // Shooting Star (small body, long upper wick) - typically after an uptrend
            if (upperShadow1 > body1 * 2 && lowerShadow1 < body1 * 0.5m)
            {
                return "Bearish Shooting Star";
            }

            // Marubozu (full body, no shadows)
            if (body1 / range1 > 0.98m)
            {
                return isC1Bullish ? "Bullish Marubozu" : "Bearish Marubozu";
            }

            return "N/A";
        }


        private string GetPatternContext(AnalysisResult r)
        {
            // --- Layer 1: Multi-Day and Structural Context (Highest Significance) ---
            if (r.MarketStructure == "Trending Up" && (r.YesterdayProfileSignal.Contains("Y-VAL") || r.YesterdayProfileSignal.Contains("Y-POC")))
            {
                return " at a Key Retracement Support";
            }
            if (r.MarketStructure == "Trending Down" && (r.YesterdayProfileSignal.Contains("Y-VAH") || r.YesterdayProfileSignal.Contains("Y-POC")))
            {
                return " at a Key Retracement Resistance";
            }

            // --- Layer 2: Intraday Extremes and Key Reference Points ---
            bool atMajorSupport = r.InitialBalanceSignal.Contains("Low") ||
                                  r.YesterdayProfileSignal.Contains("Y-VAL") ||
                                  r.DayRangeSignal == "Near Low";

            if (atMajorSupport) return " at a Confluence Support Zone";

            bool atMajorResistance = r.InitialBalanceSignal.Contains("High") ||
                                     r.YesterdayProfileSignal.Contains("Y-VAH") ||
                                     r.DayRangeSignal == "Near High";

            if (atMajorResistance) return " at a Confluence Resistance Zone";


            // --- Layer 3: Developing Value and Dynamic Levels ---
            bool atDevelopingSupport = r.VwapBandSignal == "At Lower Band" ||
                                       (r.DevelopingVal > 0 && Math.Abs(r.LTP - r.DevelopingVal) < (r.LTP * 0.0005m)); // Within 0.05%

            if (atDevelopingSupport) return " at a Dynamic Support Level";

            bool atDevelopingResistance = r.VwapBandSignal == "At Upper Band" ||
                                          (r.DevelopingVah > 0 && Math.Abs(r.LTP - r.DevelopingVah) < (r.LTP * 0.0005m));

            if (atDevelopingResistance) return " at a Dynamic Resistance Level";

            // --- Default: If no specific context is met ---
            return string.Empty; // Return an empty string if the pattern is not at a key level
        }

        private string GetVolumeConfirmation(Candle current, Candle previous) { if (previous.Volume > 0) { decimal volChange = ((decimal)current.Volume - previous.Volume) / previous.Volume; if (volChange > 0.5m) { return " (+Vol)"; } } return ""; }
        private string AnalyzeOpenType(DashboardInstrument instrument, List<Candle> oneMinCandles) { if (oneMinCandles.Count < 3) return "Analyzing Open..."; var firstCandle = oneMinCandles[0]; bool isFirstCandleStrong = Math.Abs(firstCandle.Close - firstCandle.Open) > (firstCandle.High - firstCandle.Low) * 0.7m; if (isFirstCandleStrong && firstCandle.Close > firstCandle.Open) return "Open-Drive (Bullish)"; if (isFirstCandleStrong && firstCandle.Close < firstCandle.Open) return "Open-Drive (Bearish)"; return "Open-Auction (Rotational)"; }
        private (string, decimal, decimal) CalculateVwapBandSignal(decimal ltp, List<Candle> candles) { if (candles.Count < 2) return ("N/A", 0, 0); var vwap = candles.Last().Vwap; if (vwap == 0) return ("N/A", 0, 0); decimal sumOfSquares = candles.Sum(c => (c.Close - vwap) * (c.Close - vwap)); decimal stdDev = (decimal)Math.Sqrt((double)(sumOfSquares / candles.Count)); var upperBand = vwap + (stdDev * _settingsViewModel.VwapUpperBandMultiplier); var lowerBand = vwap - (stdDev * _settingsViewModel.VwapLowerBandMultiplier); string signal = "Inside Bands"; if (ltp > upperBand) signal = "Above Upper Band"; else if (ltp < lowerBand) signal = "Below Lower Band"; return (signal, upperBand, lowerBand); }
        private decimal CalculateAnchoredVwap(List<Candle> candles) { if (candles == null || !candles.Any()) return 0; decimal cumulativePriceVolume = candles.Sum(c => c.Close * c.Volume); long cumulativeVolume = candles.Sum(c => c.Volume); return (cumulativeVolume > 0) ? cumulativePriceVolume / cumulativeVolume : 0; }
        private string GetInitialBalanceSignal(decimal ltp, MarketProfile profile, string securityId) { if (!profile.IsInitialBalanceSet) return "IB Forming"; if (!_stateManager.InitialBalanceState.ContainsKey(securityId)) _stateManager.InitialBalanceState[securityId] = (false, false); var (isBreakout, isBreakdown) = _stateManager.InitialBalanceState[securityId]; if (ltp > profile.InitialBalanceHigh && !isBreakout) { _stateManager.InitialBalanceState[securityId] = (true, false); return "IB Breakout"; } if (ltp < profile.InitialBalanceLow && !isBreakdown) { _stateManager.InitialBalanceState[securityId] = (false, true); return "IB Breakdown"; } if (ltp > profile.InitialBalanceHigh && isBreakout) return "IB Extension Up"; if (ltp < profile.InitialBalanceLow && isBreakdown) return "IB Extension Down"; return "Inside IB"; }
        private string AnalyzePriceRelativeToYesterdayProfile(decimal ltp, MarketProfileData? previousDay) { if (previousDay == null || ltp == 0) return "N/A"; if (ltp > previousDay.TpoLevelsInfo.ValueAreaHigh) return "Trading Above Y-VAH"; if (ltp < previousDay.TpoLevelsInfo.ValueAreaLow) return "Trading Below Y-VAL"; return "Trading Inside Y-Value"; }

        private string RunTier1InstitutionalIntentAnalysis(DashboardInstrument spotIndex)
        {
            string futureUnderlyingSymbol = spotIndex.Symbol switch
            {
                "Nifty 50" => "NIFTY",
                "Nifty Bank" => "BANKNIFTY",
                "Sensex" => "SENSEX",
                _ => spotIndex.Symbol
            };

            var future = _stateManager.AnalysisResults.Values.FirstOrDefault(r => r.InstrumentGroup == "FUTIDX" && r.UnderlyingGroup == futureUnderlyingSymbol);
            if (future == null) return "Neutral (Future not tracked)";

            var futureCandles = _stateManager.GetCandles(future.SecurityId, TimeSpan.FromMinutes(5));
            var spotCandles = _stateManager.GetCandles(spotIndex.SecurityId, TimeSpan.FromMinutes(5));

            if (futureCandles == null || spotCandles == null || futureCandles.Count < 2 || spotCandles.Count < 2)
            {
                return "Neutral (Building History)";
            }

            var lastFutureCandle = futureCandles.Last();
            var lastSpotCandle = spotCandles.Last();

            var prevFutureCandle = futureCandles[^2];
            var prevSpotCandle = spotCandles[^2];

            decimal currentBasis = lastFutureCandle.Close - lastSpotCandle.Close;
            decimal previousBasis = prevFutureCandle.Close - prevSpotCandle.Close;

            bool isVolumeHigh = lastFutureCandle.Volume > future.AvgVolume * 1.2m;

            if (isVolumeHigh)
            {
                if (currentBasis > previousBasis && lastFutureCandle.Close > prevFutureCandle.Close)
                {
                    return "Bullish (Premium Expansion)";
                }
                if (currentBasis < previousBasis && lastFutureCandle.Close < prevFutureCandle.Close)
                {
                    return "Bearish (Discount Expansion)";
                }
            }

            return "Neutral";
        }

        public void RunDailyBiasAnalysis(DashboardInstrument instrument, AnalysisResult result) { var profiles = _stateManager.HistoricalMarketProfiles.GetValueOrDefault(instrument.SecurityId); if (profiles == null || profiles.Count < 3) { result.DailyBias = "Insufficient History"; result.MarketStructure = "Unknown"; return; } var sortedProfiles = profiles.OrderByDescending(p => p.Date).ToList(); var p1 = sortedProfiles[0]; var p2 = sortedProfiles[1]; var p3 = sortedProfiles[2]; bool isP1Higher = p1.TpoLevelsInfo.ValueAreaLow > p2.TpoLevelsInfo.ValueAreaHigh; bool isP2Higher = p2.TpoLevelsInfo.ValueAreaLow > p3.TpoLevelsInfo.ValueAreaHigh; bool isP1OverlapHigher = p1.TpoLevelsInfo.PointOfControl > p2.TpoLevelsInfo.ValueAreaHigh; bool isP2OverlapHigher = p2.TpoLevelsInfo.PointOfControl > p3.TpoLevelsInfo.ValueAreaHigh; if ((isP1Higher && isP2Higher) || (isP1OverlapHigher && isP2OverlapHigher)) { result.MarketStructure = "Trending Up"; result.DailyBias = "Bullish"; return; } bool isP1Lower = p1.TpoLevelsInfo.ValueAreaHigh < p2.TpoLevelsInfo.ValueAreaLow; bool isP2Lower = p2.TpoLevelsInfo.ValueAreaHigh < p3.TpoLevelsInfo.ValueAreaLow; bool isP1OverlapLower = p1.TpoLevelsInfo.PointOfControl < p2.TpoLevelsInfo.ValueAreaLow; bool isP2OverlapLower = p2.TpoLevelsInfo.PointOfControl < p3.TpoLevelsInfo.ValueAreaLow; if ((isP1Lower && isP2Lower) || (isP1OverlapLower && isP2OverlapLower)) { result.MarketStructure = "Trending Down"; result.DailyBias = "Bearish"; return; } result.MarketStructure = "Balancing"; result.DailyBias = "Neutral / Rotational"; }
        public decimal GetTickSize(DashboardInstrument? instrument) => (instrument?.InstrumentType == "INDEX") ? 1.0m : 0.05m;

        private string GetHistoricalIvKey(DashboardInstrument instrument, decimal underlyingPrice)
        {
            if (underlyingPrice <= 0) return string.Empty;

            decimal strikeStep = GetStrikePriceStep(instrument.UnderlyingSymbol);
            decimal atmThreshold = strikeStep * 2;

            if (Math.Abs(instrument.StrikePrice - underlyingPrice) <= atmThreshold)
            {
                return $"{instrument.UnderlyingSymbol}_ATM_{instrument.OptionType}";
            }

            return string.Empty;
        }

        private int GetStrikePriceStep(string underlyingSymbol)
        {
            string upperSymbol = underlyingSymbol.ToUpperInvariant();
            if (upperSymbol.Contains("SENSEX") || upperSymbol.Contains("BANKNIFTY"))
            {
                return 100;
            }
            return 50; // Default for NIFTY
        }


        private (decimal ivRank, decimal ivPercentile) CalculateIvRankAndPercentile(decimal currentIv, string key)
        {
            // --- START OF MODIFIED LOGIC ---
            const int minHistoryRequired = 10; // Define the minimum number of days required.

            var ivHistory = _historicalIvService.Get90DayIvHistory(key);

            // If we don't have enough historical data yet, return 0.
            if (ivHistory == null || ivHistory.Count < minHistoryRequired)
            {
                return (0m, 0m);
            }
            // --- END OF MODIFIED LOGIC ---

            // The rest of the calculation proceeds as before if enough data exists.
            ivHistory.Add(currentIv);

            decimal histHigh = ivHistory.Max();
            decimal histLow = ivHistory.Min();

            decimal histRange = histHigh - histLow;
            decimal ivRank = (histRange > 0) ? ((currentIv - histLow) / histRange) * 100 : 0m;

            int countBelow = ivHistory.Count(iv => iv < currentIv);
            decimal ivPercentile = (ivHistory.Count > 0) ? ((decimal)countBelow / ivHistory.Count) * 100 : 0m;

            return (Math.Max(0, Math.Min(100, Math.Round(ivRank, 2))),
                    Math.Max(0, Math.Min(100, Math.Round(ivPercentile, 2))));
        }
        private string GenerateVolatilityStateSignal(DashboardInstrument instrument, AnalysisResult result) { bool isAtrContracting = result.AtrSignal5Min == "Vol Contracting"; bool isIvRankLow = result.IvRank < 30; if (isAtrContracting && isIvRankLow) { _stateManager.IsInVolatilitySqueeze[instrument.SecurityId] = true; return "IV Squeeze Setup"; } return "Normal Volatility"; }

        #endregion
    }
}