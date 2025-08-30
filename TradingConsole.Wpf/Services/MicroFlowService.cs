// TradingConsole.Wpf/Services/Analysis/MicroFlowService.cs
using System;
using System.Collections.Concurrent;
using System.Linq;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Wpf.Services.Analysis
{
    public class MicroFlowService
    {
        private readonly AnalysisStateManager _stateManager;
        private const int TimeWindowSeconds = 15;
        private const decimal DominanceThreshold = 0.65m; // 65%

        public MicroFlowService(AnalysisStateManager stateManager)
        {
            _stateManager = stateManager;
        }

        public void AddTick(string securityId, decimal price, long volume)
        {
            if (!_stateManager.RecentTicks.TryGetValue(securityId, out var tickQueue))
            {
                return;
            }

            var now = DateTime.UtcNow;

            // Add the new tick
            tickQueue.Enqueue(new TickData { Timestamp = now, Price = price, Volume = volume });

            // Prune old ticks from the queue
            while (tickQueue.TryPeek(out var oldestTick) && (now - oldestTick.Timestamp).TotalSeconds > TimeWindowSeconds)
            {
                tickQueue.TryDequeue(out _);
            }
        }

        public string AnalyzeMicroFlow(string securityId)
        {
            if (!_stateManager.RecentTicks.TryGetValue(securityId, out var tickQueue) || tickQueue.Count < 2)
            {
                return "Building...";
            }

            long buyingVolume = 0;
            long sellingVolume = 0;
            long neutralVolume = 0;
            decimal lastPrice = 0;

            foreach (var tick in tickQueue)
            {
                if (lastPrice == 0)
                {
                    lastPrice = tick.Price;
                    continue;
                }

                if (tick.Price > lastPrice)
                {
                    buyingVolume += tick.Volume;
                }
                else if (tick.Price < lastPrice)
                {
                    sellingVolume += tick.Volume;
                }
                else
                {
                    neutralVolume += tick.Volume;
                }

                lastPrice = tick.Price;
            }

            long totalVolume = buyingVolume + sellingVolume + neutralVolume;
            if (totalVolume == 0)
            {
                return "Neutral Flow";
            }

            decimal buyingDominance = (decimal)buyingVolume / totalVolume;
            decimal sellingDominance = (decimal)sellingVolume / totalVolume;

            if (buyingDominance >= DominanceThreshold)
            {
                return "Aggressive Buying";
            }
            if (sellingDominance >= DominanceThreshold)
            {
                return "Aggressive Selling";
            }

            return "Neutral Flow";
        }
    }
}