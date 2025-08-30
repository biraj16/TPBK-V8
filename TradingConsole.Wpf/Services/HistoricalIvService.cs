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
    /// Manages loading and saving historical Implied Volatility data.
    /// </summary>
    public class HistoricalIvService
    {
        private readonly string _filePath;
        private HistoricalIvDatabase _database;

        public HistoricalIvService()
        {
            string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string appFolderPath = Path.Combine(appDataPath, "TradingConsole");
            Directory.CreateDirectory(appFolderPath);
            _filePath = Path.Combine(appFolderPath, "historical_iv.json");

            _database = LoadDatabase();
        }

        private HistoricalIvDatabase LoadDatabase()
        {
            if (!File.Exists(_filePath))
            {
                return new HistoricalIvDatabase();
            }

            try
            {
                string json = File.ReadAllText(_filePath);
                var db = JsonSerializer.Deserialize<HistoricalIvDatabase>(json);
                return db ?? new HistoricalIvDatabase();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HistoricalIvService] Error loading IV database: {ex.Message}");
                return new HistoricalIvDatabase(); // Return a fresh DB if file is corrupt
            }
        }

        public void SaveDatabase()
        {
            try
            {
                PruneOldRecords();
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(_database, options);
                File.WriteAllText(_filePath, json);
                Debug.WriteLine("[HistoricalIvService] Successfully saved IV database.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HistoricalIvService] Error saving IV database: {ex.Message}");
            }
        }

        public void RecordIvSnapshot(string key, decimal currentIv)
        {
            if (string.IsNullOrEmpty(key) || currentIv <= 0) return;

            if (!_database.Records.ContainsKey(key))
            {
                _database.Records[key] = new List<DailyIvRecord>();
            }

            // If a snapshot for today already exists, do nothing. This ensures we only capture the first one.
            if (_database.Records[key].Any(r => r.Date.Date == DateTime.Today))
            {
                return;
            }

            // Add the new snapshot record for the current day.
            _database.Records[key].Add(new DailyIvRecord
            {
                Date = DateTime.Today,
                SnapshotIv = currentIv,
                SnapshotTimestamp = DateTime.UtcNow // Store the UTC timestamp for reference
            });

            Debug.WriteLine($"[HistoricalIvService] Recorded IV snapshot for {key}: {currentIv}");
        }

        // --- THE FIX: This method now returns a list of historical snapshot values ---
        public List<decimal> Get90DayIvHistory(string key)
        {
            if (!_database.Records.ContainsKey(key))
            {
                return new List<decimal>();
            }

            var ninetyDaysAgo = DateTime.Today.AddDays(-90);

            return _database.Records[key]
                .Where(r => r.Date >= ninetyDaysAgo)
                .Select(r => r.SnapshotIv)
                .ToList();
        }

        private void PruneOldRecords()
        {
            var ninetyDaysAgo = DateTime.Today.AddDays(-90);
            foreach (var key in _database.Records.Keys)
            {
                _database.Records[key].RemoveAll(r => r.Date < ninetyDaysAgo);
            }
        }
    }
}
