// In TradingConsole.Wpf/Services/Analysis/MarketInternalsService.cs
using System;
using System.Collections.Generic;
using System.Linq;
using TradingConsole.Core.Models;
using TradingConsole.Wpf.ViewModels;

namespace TradingConsole.Wpf.Services.Analysis
{
    public class MarketInternalsService
    {
        private readonly SettingsViewModel _settingsViewModel;

        public MarketInternalsService(SettingsViewModel settingsViewModel)
        {
            _settingsViewModel = settingsViewModel;
        }

        public int CalculateParticipationScore(DashboardInstrument nifty, IEnumerable<DashboardInstrument> allInstruments)
        {
            if (nifty.Open == 0) return 50;

            // --- FIX #1: Use the NiftyHeavyweightsDictionary property ---
            // This provides the data as a Dictionary<string, double>, which has the ContainsKey method.
            var niftyHeavyweights = _settingsViewModel.NiftyHeavyweightsDictionary;

            var heavyweightsData = allInstruments
                .Where(inst => inst.UnderlyingSymbol != null && niftyHeavyweights.ContainsKey(inst.UnderlyingSymbol.ToUpper()))
                .ToList();

            if (!heavyweightsData.Any()) return 0;

            double totalWeight = 0;
            double weightedChange = 0;

            foreach (var stock in heavyweightsData)
            {
                if (stock.Open > 0)
                {
                    // --- FIX #2: Access the .Weight property of the HeavyweightEntry ---
                    // The niftyHeavyweights dictionary already contains the correct double value.
                    var stockWeight = niftyHeavyweights[stock.UnderlyingSymbol.ToUpper()];
                    var stockPctChange = (double)(stock.LTP - stock.Open) / (double)stock.Open;
                    weightedChange += stockPctChange * stockWeight;
                    totalWeight += stockWeight; // This correctly adds a double to a double.
                }
            }

            if (totalWeight == 0) return 50;

            var weightedAvgHeavyweightChange = weightedChange / totalWeight;
            var niftyPctChange = (double)(nifty.LTP - nifty.Open) / (double)nifty.Open;

            if (Math.Sign(niftyPctChange) != Math.Sign(weightedAvgHeavyweightChange) && Math.Abs(niftyPctChange) > 0.001)
            {
                return 10;
            }

            double difference = Math.Abs(niftyPctChange - weightedAvgHeavyweightChange);
            int score = 100 - (int)(Math.Min(difference, 0.01) * 10000);

            return Math.Max(0, Math.Min(100, score));
        }
    }
}