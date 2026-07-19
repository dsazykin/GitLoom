using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LibGit2Sharp;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.Controls;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-13 diff viewer offscreen so the intra-line (word-level) emphasis and the
// trailing-whitespace marker can be visually confirmed. The dispatcher is pumped until the diff
// has loaded and laid out before capture — a control that measured 0 width before its data arrived
// would render an empty frame, which the inline-count assertions guard against.
public class DiffQualityRenderHarness
{
    [AvaloniaFact]
    public void Capture_IntraLineEmphasis()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        // A single-word change (cat->dog) plus an added line carrying trailing whitespace.
        fx.CommitFile("Sample.cs", "alpha\nthe cat sat here\nbeta\ngamma\n", "seed");
        fx.WriteFile("Sample.cs", "alpha\nthe dog sat here\nbeta\ngamma\nadded trailing   \n");

        var vm = new DiffViewerViewModel(git, fx.RepoPath);
        var view = new DiffViewerView { DataContext = vm };
        var win = new Window { Content = view, Width = 1000, Height = 640 };
        win.Show();

        vm.UpdateDiff(new GitFileStatus { FilePath = "Sample.cs", State = FileStatus.ModifiedInWorkdir });
        Settle();

        // Data reached the VM: at least one changed line has intra-line spans.
        var changed = vm.Hunks.SelectMany(h => h.Lines).Where(l => l.IsChange).ToList();
        Assert.Contains(changed, l => l.HighlightSpans.Count > 0);

        // The emphasis actually rendered: an IntraLineDiffTextBlock split its text into multiple
        // Runs (an unchanged line collapses to a single Run; an emphasized line does not).
        var emphasized = view.GetVisualDescendants().OfType<IntraLineDiffTextBlock>()
            .Where(t => t.Inlines != null && t.Inlines.OfType<Run>().Any(r => r.Background != null))
            .ToList();
        Assert.NotEmpty(emphasized);

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "diff_quality_intraline_unified.png"));

        // Side-by-side view also carries the emphasis.
        vm.IsSideBySideView = true;
        Settle();
        var sbsEmphasized = view.GetVisualDescendants().OfType<IntraLineDiffTextBlock>()
            .Any(t => t.Inlines != null && t.Inlines.OfType<Run>().Any(r => r.Background != null));
        Assert.True(sbsEmphasized);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "diff_quality_intraline_sidebyside.png"));
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_IgnoreWhitespace_HidesPartialStaging()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("f.cs", "class C\n{\n    int x;\n    int y;\n}\n", "seed");
        // Whitespace-only reindent of one line + a real content change on another.
        fx.WriteFile("f.cs", "class C\n{\n\tint x;\n    int z;\n}\n");

        var vm = new DiffViewerViewModel(git, fx.RepoPath);
        var view = new DiffViewerView { DataContext = vm };
        var win = new Window { Content = view, Width = 1000, Height = 640 };
        win.Show();

        vm.UpdateDiff(new GitFileStatus { FilePath = "f.cs", State = FileStatus.ModifiedInWorkdir });
        Settle();
        Assert.True(vm.PartialStagingAvailable);

        vm.IgnoreWhitespace = true;   // re-runs `git diff -w`
        Settle();

        Assert.False(vm.PartialStagingAvailable);
        // The whitespace-only reindent is gone; only the genuine change remains.
        Assert.Contains(vm.Hunks.SelectMany(h => h.Lines), l => l.DisplayText.Contains("int z;"));
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "diff_quality_ignore_whitespace.png"));
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 12; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
