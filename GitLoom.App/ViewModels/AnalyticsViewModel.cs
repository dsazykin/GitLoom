using CommunityToolkit.Mvvm.ComponentModel;
using GitLoom.Core.Analytics;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GitLoom.App.ViewModels
{
    public partial class AnalyticsViewModel : ViewModelBase
    {
        private readonly RepositoryAnalyzer _analyzer;
        private readonly string _repositoryPath;

        [ObservableProperty]
        private bool _isLoading = true;

        [ObservableProperty]
        private ISeries[]? _languageSeries;

        [ObservableProperty]
        private ISeries[]? _punchCardSeries;

        public Axis[] XAxes { get; set; } =
        {
            new Axis
            {
                Labels = new[] { "12 AM", "1 AM", "2 AM", "3 AM", "4 AM", "5 AM", "6 AM", "7 AM", "8 AM", "9 AM", "10 AM", "11 AM", "12 PM", "1 PM", "2 PM", "3 PM", "4 PM", "5 PM", "6 PM", "7 PM", "8 PM", "9 PM", "10 PM", "11 PM" },
                TextSize = 12,
                LabelsPaint = new SolidColorPaint(SKColors.Gray)
            }
        };

        public Axis[] YAxes { get; set; } =
        {
            new Axis
            {
                Labels = new[] { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" },
                TextSize = 12,
                LabelsPaint = new SolidColorPaint(SKColors.Gray)
            }
        };

        public AnalyticsViewModel(string repositoryPath)
        {
            _repositoryPath = repositoryPath;
            _analyzer = new RepositoryAnalyzer();
            
            // Fire and forget data loading
            _ = LoadAnalyticsAsync();
        }

        private async Task LoadAnalyticsAsync()
        {
            IsLoading = true;

            var languagesTask = _analyzer.CalculateLanguageBreakdownAsync(_repositoryPath);
            var punchCardTask = _analyzer.GeneratePunchCardAsync(_repositoryPath);

            await Task.WhenAll(languagesTask, punchCardTask);

            var languageData = languagesTask.Result;
            var punchCardData = punchCardTask.Result;

            BuildLanguageSeries(languageData);
            BuildPunchCardSeries(punchCardData);

            IsLoading = false;
        }

        private void BuildLanguageSeries(Dictionary<LanguageModel, long> data)
        {
            var totalBytes = data.Values.Sum();
            if (totalBytes == 0) return;

            var series = new List<ISeries>();

            // Only take top 8 to avoid chart clutter
            var topLanguages = data.OrderByDescending(x => x.Value).Take(8);

            foreach (var lang in topLanguages)
            {
                if (SKColor.TryParse(lang.Key.Color, out SKColor parsedColor))
                {
                    series.Add(new PieSeries<long>
                    {
                        Values = new[] { lang.Value },
                        Name = lang.Key.Name,
                        Fill = new SolidColorPaint(parsedColor),
                        InnerRadius = 60,
                        ToolTipLabelFormatter = point => $"{lang.Key.Name}: {point.Coordinate.PrimaryValue:N0} bytes"
                    });
                }
            }

            LanguageSeries = series.ToArray();
        }

        private void BuildPunchCardSeries(PunchCardStats stats)
        {
            var scatterData = new ObservableCollection<WeightedPoint>();
            foreach (var point in stats.GetDataPoints())
            {
                scatterData.Add(new WeightedPoint(point.Hour, point.DayOfWeek, point.CommitCount));
            }

            PunchCardSeries = new ISeries[]
            {
                new ScatterSeries<WeightedPoint>
                {
                    Values = scatterData,
                    Fill = new SolidColorPaint(SKColors.Cyan),
                    Name = "Commits"
                }
            };
        }
    }
}
