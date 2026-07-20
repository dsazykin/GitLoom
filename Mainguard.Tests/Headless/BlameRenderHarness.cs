using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-11 blame view offscreen (headless Skia): the editor showing the file text with the
// blame gutter margin beside it — age-heat bar, author · shortSha · relative-date, and alternating
// dim on commit boundaries. PNG to artifacts_headless/ for the human visual pass (the gutter's
// visual polish is deferred; this proves it is functionally wired and renders).
public class BlameRenderHarness
{
    [AvaloniaFact]
    public void Capture_BlameGutter()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();

        // Three commits by different authors at spread-out dates so the age-heat ramp and the
        // per-commit boundary shading are both visible.
        var body = string.Join("\n", Enumerable.Range(1, 10).Select(n => $"line {n} of source")) + "\n";
        var sha1 = fx.CommitFile("Source.cs", body, "initial import",
            "Ada Lovelace", "ada@gitloom.local", DateTimeOffset.Now.AddDays(-420));

        var v2 = body.Replace("line 3 of source", "line 3 REWORKED").Replace("line 4 of source", "line 4 REWORKED");
        fx.CommitFile("Source.cs", v2, "rework lines 3-4",
            "Grace Hopper", "grace@gitloom.local", DateTimeOffset.Now.AddDays(-35));

        var v3 = v2.Replace("line 8 of source", "line 8 TODAY").Replace("line 9 of source", "line 9 TODAY");
        fx.CommitFile("Source.cs", v3, "touch lines 8-9",
            "Linus Torvalds", "linus@gitloom.local", DateTimeOffset.Now.AddHours(-2));

        var vm = new BlameViewModel(git, fx.RepoPath) { IsBlameVisible = true };
        var view = new BlameView { DataContext = vm };
        var win = new Window { Content = view, Width = 900, Height = 460 };
        win.Show();

        _ = vm.LoadAsync("Source.cs");
        Pump(() => vm.BlameLines.Count > 0 && !vm.IsLoading);
        Settle();

        // Blame is wired end-to-end: one row per line, mapped to the right commits/authors.
        Assert.Equal(10, vm.BlameLines.Count);
        Assert.Equal(sha1, vm.BlameLines[0].Sha);                 // line 1 still the initial commit
        Assert.Equal("Grace Hopper", vm.BlameLines[2].AuthorName); // line 3 reworked by Grace
        Assert.Equal("Linus Torvalds", vm.BlameLines[7].AuthorName); // line 8 touched today
        Assert.Equal(3, vm.BlameLines.Select(b => b.Sha).Distinct().Count());

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "blame_gutter.png"));
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static void Pump(Func<bool> until)
    {
        for (int i = 0; i < 200 && !until(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
