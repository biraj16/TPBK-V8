// TradingConsole.Wpf/ViewModels/GexViewModel.cs
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using TradingConsole.Core.Models;

namespace TradingConsole.Wpf.ViewModels
{
    public class GexViewModel : ObservableModel
    {
        private decimal _maxGexLevel;
        public decimal MaxGexLevel { get => _maxGexLevel; set => SetProperty(ref _maxGexLevel, value); }

        private decimal _gexFlipPoint;
        public decimal GexFlipPoint { get => _gexFlipPoint; set => SetProperty(ref _gexFlipPoint, value); }

        private decimal _netGex;
        public decimal NetGex { get => _netGex; set => SetProperty(ref _netGex, value); }

        private decimal _underlyingPrice;
        public decimal UnderlyingPrice { get => _underlyingPrice; set => SetProperty(ref _underlyingPrice, value); }

        public ObservableCollection<ISeries> GexChartSeries { get; set; } = new ObservableCollection<ISeries>();
        public Axis[] GexXAxes { get; set; } = new Axis[0];
        public Axis[] GexYAxes { get; set; } = new Axis[0];

        public GexViewModel()
        {
            SetupChart();
        }

        private void SetupChart()
        {
            GexXAxes = new Axis[]
            {
                new Axis
                {
                    Name = "Strike Price",
                    LabelsPaint = new SolidColorPaint(SKColors.LightGray),
                    SeparatorsPaint = new SolidColorPaint(SKColors.Gray.WithAlpha(50))
                }
            };

            GexYAxes = new Axis[]
            {
                new Axis
                {
                    Name = "Gamma Exposure (per 1% move)",
                    LabelsPaint = new SolidColorPaint(SKColors.LightGray),
                    SeparatorsPaint = new SolidColorPaint(SKColors.Gray.WithAlpha(50))
                }
            };
        }

        public void UpdateGexData(IEnumerable<OptionChainRow> optionChain, decimal underlyingPrice)
        {
            if (optionChain == null || !optionChain.Any()) return;

            UnderlyingPrice = underlyingPrice;
            var gexValues = new List<ObservablePoint>();
            decimal totalCallGex = 0;
            decimal totalPutGex = 0;
            decimal maxGexValue = 0;

            foreach (var row in optionChain)
            {
                // GEX ($) = Gamma * OI * 100 * (Spot Price)^2 * 0.01
                var callGex = (row.CallOption?.Gamma ?? 0) * (row.CallOption?.OI ?? 0) * 100 * (underlyingPrice * underlyingPrice) * 0.0001m;
                var putGex = (row.PutOption?.Gamma ?? 0) * (row.PutOption?.OI ?? 0) * 100 * (underlyingPrice * underlyingPrice) * 0.0001m;
                var netGexAtStrike = callGex - putGex;

                gexValues.Add(new ObservablePoint((double)row.StrikePrice, (double)netGexAtStrike));

                totalCallGex += callGex;
                totalPutGex -= putGex; // Puts have negative gamma effect

                if (Math.Abs(netGexAtStrike) > Math.Abs(maxGexValue))
                {
                    maxGexValue = netGexAtStrike;
                    MaxGexLevel = row.StrikePrice;
                }
            }

            NetGex = totalCallGex + totalPutGex;
            GexFlipPoint = (from row in optionChain
                            orderby row.StrikePrice
                            where ((row.CallOption?.Gamma ?? 0) * (row.CallOption?.OI ?? 0)) > ((row.PutOption?.Gamma ?? 0) * (row.PutOption?.OI ?? 0))
                            select row.StrikePrice).LastOrDefault();

            GexChartSeries.Clear();
            GexChartSeries.Add(new ColumnSeries<ObservablePoint>
            {
                Values = gexValues,
                Name = "Net GEX",
                Stroke = null,
                Fill = new SolidColorPaint(SKColors.CornflowerBlue),
                IgnoresBarPosition = true
            });
        }
    }
}