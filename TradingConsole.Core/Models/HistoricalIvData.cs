// In TradingConsole.Core/Models/HistoricalIvData.cs
using System;
using System.Collections.Generic;

namespace TradingConsole.Core.Models
{
    public class DailyIvRecord
    {
        public DateTime Date { get; set; }

        // single snapshot value and its timestamp 
        public decimal SnapshotIv { get; set; }
        public DateTime SnapshotTimestamp { get; set; }
    }

    public class HistoricalIvDatabase
    {
        public Dictionary<string, List<DailyIvRecord>> Records { get; set; } = new Dictionary<string, List<DailyIvRecord>>();
    }
}