using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-22 analytics view offscreen (headless Skia) so the four LiveCharts — language donut,
// weekly churn time series, commit punch-card heatmap, and top-contributor bars — can be inspected for
// real data, axes, legends and light/dark legibility. Builds a realistic multi-author, multi-week,
// multi-language fixture, waits for the async analysis + a chart render tick, then captures PNGs to
// artifacts_headless/ (visual review, not pass/fail). Charts drawn before layout is a common LiveCharts
// bug, so we pump the dispatcher until IsLoading clears and then some.
public class AnalyticsRenderHarness
{
    [AvaloniaFact]
    public void Capture_AnalyticsView_DarkAndLight()
    {
        using var fx = BuildRepo();

        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
            CaptureOnce(fx.RepoPath, "analytics_dark.png");

            ThemeManager.Apply("DaylightLoom", persist: false);
            CaptureOnce(fx.RepoPath, "analytics_light.png");
        }
        finally
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    private static void CaptureOnce(string repoPath, string fileName)
    {
        // Build the VM AFTER the theme switch so the token-derived chart paints resolve for this theme.
        var vm = new AnalyticsViewModel(repoPath, new GitService());
        var view = new AnalyticsView { DataContext = vm };
        var win = new Window { Content = view, Width = 900, Height = 1850 };
        win.Show();

        // Wait for the background analysis to complete, then give LiveCharts render ticks to lay out.
        for (int i = 0; i < 60 && vm.IsLoading; i++) Pump();
        for (int i = 0; i < 40; i++) Pump();

        Assert.False(vm.IsLoading);
        Assert.True(vm.HasCommitData);
        Assert.True(vm.HasLanguageData);
        Assert.NotNull(vm.LanguageSeries);
        Assert.NotNull(vm.ChurnSeries);
        Assert.NotNull(vm.PunchCardSeries);
        Assert.NotNull(vm.ContributorSeries);

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), fileName));
        HarnessHygiene.Teardown(win);
        vm.Dispose();
    }

    private static TempRepoFixture BuildRepo()
    {
        var fx = new TempRepoFixture();

        // Multiple languages of differing size → a multi-slice donut.
        fx.WriteFile("src/app.cs", new string('/', 40) + "\nclass App { }\n");
        fx.WriteFile("src/core.cs", new string('/', 120) + "\nclass Core { }\n");
        fx.WriteFile("web/index.js", "export const app = () => {};\n");
        fx.WriteFile("scripts/build.py", "print('build')\n");
        fx.WriteFile("web/style.css", "body { margin: 0; }\n");

        // Commits spread across four weeks, three authors, varied weekday/hour → churn, punch, bars.
        (string name, string email)[] authors =
        {
            ("Ada Lovelace", "ada@x.io"),
            ("Grace Hopper", "grace@x.io"),
            ("Alan Turing", "alan@x.io"),
        };
        var start = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero); // Monday
        int a = 0;
        for (int week = 0; week < 4; week++)
        {
            int commitsThisWeek = 2 + week; // 2,3,4,5 → a rising churn trend
            for (int c = 0; c < commitsThisWeek; c++)
            {
                var author = authors[a++ % authors.Length];
                var when = start
                    .AddDays(week * 7 + (c % 5))          // spread across weekdays
                    .AddHours((c * 3) % 12);              // spread across hours
                var body = string.Join("\n", System.Linq.Enumerable.Range(0, 3 + c)) + "\n";
                fx.CommitFile("src/app.cs", $"// w{week} c{c}\n{body}", $"feat: week {week} change {c}",
                    author.name, author.email, when);
            }
        }

        return fx;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(25);
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
