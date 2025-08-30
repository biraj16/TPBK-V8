// TradingConsole.Wpf/Services/Analysis/AnalysisStateManager.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using TradingConsole.Core.Models;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Wpf.Services.Analysis
{
    // A simple struct to hold individual tick data for micro-flow analysis
    public struct TickData
    {
        public DateTime Timestamp { get; set; }
        public decimal Price { get; set; }
        public long Volume { get; set; }
    }

    /// <summary>
    /// Manages the various state dictionaries required for real-time market analysis.
    /// This includes states for indicators, market profiles, IV, candles, and more.
    /// </summary>
    public class AnalysisStateManager
    {
        public MarketPhase CurrentMarketPhase { get; set; } = MarketPhase.PreOpen;

        // --- MODIFIED: All Dictionaries are now ConcurrentDictionaries for thread-safety ---
        public ConcurrentDictionary<string, AnalysisResult> AnalysisResults { get; } = new();
        public ConcurrentDictionary<string, MarketProfile> MarketProfiles { get; } = new();
        public ConcurrentDictionary<string, List<MarketProfileData>> HistoricalMarketProfiles { get; } = new();

        public HashSet<string> BackfilledInstruments { get; } = new();
        public ConcurrentDictionary<string, (decimal cumulativePriceVolume, long cumulativeVolume, List<decimal> ivHistory)> TickAnalysisState { get; } = new();

        public ConcurrentDictionary<string, ConcurrentDictionary<TimeSpan, List<Candle>>> MultiTimeframeCandles { get; } = new();
        public ConcurrentDictionary<string, ConcurrentDictionary<TimeSpan, EmaState>> MultiTimeframePriceEmaState { get; } = new();
        public ConcurrentDictionary<string, ConcurrentDictionary<TimeSpan, EmaState>> MultiTimeframeVwapEmaState { get; } = new();
        public ConcurrentDictionary<string, ConcurrentDictionary<TimeSpan, RsiState>> MultiTimeframeRsiState { get; } = new();
        public ConcurrentDictionary<string, ConcurrentDictionary<TimeSpan, AtrState>> MultiTimeframeAtrState { get; } = new();
        public ConcurrentDictionary<string, ConcurrentDictionary<TimeSpan, ObvState>> MultiTimeframeObvState { get; } = new();

        // --- NEW: A dictionary to hold the ticks for the *currently forming* candle for smoothing ---
        public ConcurrentDictionary<string, ConcurrentDictionary<TimeSpan, List<TickData>>> FormingCandleTicks { get; } = new();
        public ConcurrentDictionary<string, ActivePatternState> ActiveCandlestickPatterns { get; } = new();

        // --- NEW: Added a dictionary to hold the queue of recent ticks for micro-flow analysis ---
        public ConcurrentDictionary<string, ConcurrentQueue<TickData>> RecentTicks { get; } = new();

        public ConcurrentDictionary<string, IntradayIvState> IntradayIvStates { get; } = new();
        public ConcurrentDictionary<string, IntradayIvState.CustomLevelState> CustomLevelStates { get; } = new();
        public ConcurrentDictionary<string, (bool isBreakout, bool isBreakdown)> InitialBalanceState { get; } = new();

        public ConcurrentDictionary<string, RelativeStrengthState> RelativeStrengthStates { get; } = new();
        public ConcurrentDictionary<string, IvSkewState> IvSkewStates { get; } = new();
        public ConcurrentDictionary<string, DateTime> LastSignalTime { get; } = new();
        public ConcurrentDictionary<string, bool> IsInVolatilitySqueeze { get; } = new();

        // --- NEW: State for enhanced analysis ---
        public ConcurrentDictionary<string, List<decimal>> SkewRatioHistory { get; } = new();


        private readonly List<TimeSpan> _timeframes = new()
        {
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15)
        };

        public void InitializeStateForInstrument(string securityId, string symbol, string instrumentType, string underlyingSymbol)
        {
            if (BackfilledInstruments.Contains(securityId)) return;

            BackfilledInstruments.Add(securityId);
            AnalysisResults[securityId] = new AnalysisResult { SecurityId = securityId, Symbol = symbol, InstrumentGroup = instrumentType, UnderlyingGroup = underlyingSymbol };
            TickAnalysisState[securityId] = (0, 0, new List<decimal>());
            MultiTimeframeCandles[securityId] = new ConcurrentDictionary<TimeSpan, List<Candle>>();
            MultiTimeframePriceEmaState[securityId] = new ConcurrentDictionary<TimeSpan, EmaState>();
            MultiTimeframeVwapEmaState[securityId] = new ConcurrentDictionary<TimeSpan, EmaState>();
            MultiTimeframeRsiState[securityId] = new ConcurrentDictionary<TimeSpan, RsiState>();
            MultiTimeframeAtrState[securityId] = new ConcurrentDictionary<TimeSpan, AtrState>();
            MultiTimeframeObvState[securityId] = new ConcurrentDictionary<TimeSpan, ObvState>();
            FormingCandleTicks[securityId] = new ConcurrentDictionary<TimeSpan, List<TickData>>(); // Initialize the new dictionary
            RecentTicks[securityId] = new ConcurrentQueue<TickData>(); // Initialize the tick queue
            IsInVolatilitySqueeze[securityId] = false;

            if (instrumentType == "INDEX")
            {
                RelativeStrengthStates[securityId] = new RelativeStrengthState();
                IvSkewStates[securityId] = new IvSkewState();
                CustomLevelStates[symbol] = new IntradayIvState.CustomLevelState();
                SkewRatioHistory[securityId] = new List<decimal>(); // Initialize for skew ratio
            }

            foreach (var tf in _timeframes)
            {
                MultiTimeframeCandles[securityId][tf] = new List<Candle>();
                MultiTimeframePriceEmaState[securityId][tf] = new EmaState();
                MultiTimeframeVwapEmaState[securityId][tf] = new EmaState();
                MultiTimeframeRsiState[securityId][tf] = new RsiState();
                MultiTimeframeAtrState[securityId][tf] = new AtrState();
                MultiTimeframeObvState[securityId][tf] = new ObvState();
                FormingCandleTicks[securityId][tf] = new List<TickData>(); // Initialize the list for each timeframe
            }
        }

        public List<Candle>? GetCandles(string securityId, TimeSpan timeframe)
        {
            if (MultiTimeframeCandles.TryGetValue(securityId, out var timeframes) &&
                timeframes.TryGetValue(timeframe, out var candles))
            {
                return candles;
            }
            return null;
        }

        public AnalysisResult GetResult(string securityId)
        {
            // ConcurrentDictionary's GetOrAdd is a thread-safe way to get an existing item or create it if it doesn't exist.
            return AnalysisResults.GetOrAdd(securityId, new AnalysisResult { SecurityId = securityId });
        }
    }
}