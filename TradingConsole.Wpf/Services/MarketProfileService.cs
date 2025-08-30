// In TradingConsole.Wpf/Services/MarketProfileService.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using TradingConsole.Core.Models;

namespace TradingConsole.Wpf.Services
{
    /// <summary>
    /// Manages loading and saving historical Market Profile data to a persistent file.
    /// </summary>
    public class MarketProfileService
    {
        private readonly string _filePath;
        private HistoricalMarketProfileDatabase _database;

        public MarketProfileService()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolderPath = Path.Combine(appDataPath, "TradingConsole");
            Directory.CreateDirectory(appFolderPath);
            _filePath = Path.Combine(appFolderPath, "historical_market_profile.json");

            _database = LoadDatabase();
        }

        private HistoricalMarketProfileDatabase LoadDatabase()
        {
            if (!File.Exists(_filePath))
            {
                return new HistoricalMarketProfileDatabase();
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                var db = JsonSerializer.Deserialize<HistoricalMarketProfileDatabase>(json);
                return db ?? new HistoricalMarketProfileDatabase();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MarketProfileService] Error loading profile database: {ex.Message}");
                return new HistoricalMarketProfileDatabase();
            }
        }

        public void SaveDatabase()
        {
            try
            {
                PruneAndSummarizeDatabase();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_database, options);
                File.WriteAllText(_filePath, json);
                Debug.WriteLine("[MarketProfileService] Successfully saved and pruned profile database.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MarketProfileService] Error saving profile database: {ex.Message}");
            }
        }

        public void UpdateProfile(string securityId, MarketProfileData profileData)
        {
            if (string.IsNullOrEmpty(securityId)) return;

            if (!_database.Records.ContainsKey(securityId))
            {
                _database.Records[securityId] = new List<MarketProfileData>();
            }

            // --- BUG FIX: Check for the date from the incoming data, not DateTime.Today ---
            var existingRecord = _database.Records[securityId].FirstOrDefault(r => r.Date.Date == profileData.Date.Date);

            if (existingRecord != null)
            {
                // Update the existing record for that specific day
                existingRecord.TpoLevelsInfo = profileData.TpoLevelsInfo;
                existingRecord.VolumeProfileInfo = profileData.VolumeProfileInfo;
                existingRecord.TpoCounts = profileData.TpoCounts;
                existingRecord.VolumeLevels = profileData.VolumeLevels;
            }
            else
            {
                // --- BUG FIX: Do NOT overwrite the date. Add the new record with its original date. ---
                _database.Records[securityId].Add(profileData);
            }
        }

        public List<MarketProfileData> GetHistoricalProfiles(string securityId)
        {
            if (_database.Records.TryGetValue(securityId, out var profiles))
            {
                return profiles.OrderByDescending(p => p.Date).ToList();
            }
            return new List<MarketProfileData>();
        }

        private void PruneAndSummarizeDatabase()
        {
            // --- THE FIX: Make pruning logic aware of trading days ---

            // Get the last 10 valid trading days from today.
            var recentTradingDays = new List<DateTime>();
            var currentDate = DateTime.Today;
            while (recentTradingDays.Count < 10)
            {
                if (currentDate.DayOfWeek != DayOfWeek.Saturday && currentDate.DayOfWeek != DayOfWeek.Sunday)
                {
                    // In a more advanced implementation, you would also check against a list of market holidays.
                    recentTradingDays.Add(currentDate);
                }
                currentDate = currentDate.AddDays(-1);
            }

            var tenTradingDaysAgo = recentTradingDays.Last();
            var threeTradingDaysAgo = recentTradingDays.ElementAtOrDefault(3); // The third most recent trading day.

            foreach (var key in _database.Records.Keys.ToList())
            {
                var records = _database.Records[key];

                // 1. Remove any records that are older than the last 10 trading days.
                records.RemoveAll(r => r.Date < tenTradingDaysAgo);

                // 2. For records older than the last 3 trading days, remove detailed volume data.
                foreach (var record in records)
                {
                    if (threeTradingDaysAgo != default && record.Date < threeTradingDaysAgo)
                    {
                        record.TpoCounts = null;
                        record.VolumeLevels = null;
                    }
                }
            }
        }
    }
}
