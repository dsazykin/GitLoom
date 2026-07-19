using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// Drives the T-09 drag-to-rebase/merge POINTER GESTURE end-to-end in a headless CommitTimelineView:
// it injects press → move-past-threshold → move-onto-another-chip → release and proves the gesture
// resolves the correct source+target and produces the two-action flyout (BuildDragActionMenu). Also
// captures a mid-drag frame (ghost + drop-target highlight) for visual inspection.
public class LabelDragRenderHarness
{
    [AvaloniaFact]
    public void DragLabelOntoLabel_ShouldOpenMergeRebaseFlyout()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();

        // Two branch tips on two different commits → two draggable chips on two rows.
        fx.CommitFile("a.txt", "one\n", "first");     // default branch tip (commit 1)
        fx.CreateBranch("topic");
        fx.Checkout("topic");
        fx.CommitFile("b.txt", "two\n", "second");    // topic tip (commit 2)

        var vm = new CommitTimelineViewModel(git, fx.RepoPath);
        var view = new CommitTimelineView { DataContext = vm };
        var win = new Window { Content = view, Width = 1000, Height = 600 };
        win.Show();

        vm.LoadInitialCommits();
        Settle();

        // The rendered chips (each carries a RefLabelViewModel).
        var chips = view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("RefChip") && b.DataContext is RefLabelViewModel)
            .ToList();
        Assert.True(chips.Count >= 2, $"expected >=2 ref chips, got {chips.Count}");

        var sourceChip = chips[0];
        var targetChip = chips.First(c => !ReferenceEquals(c, sourceChip)
            && ((RefLabelViewModel)c.DataContext!).RefName != ((RefLabelViewModel)sourceChip.DataContext!).RefName);
        var sourceRef = ((RefLabelViewModel)sourceChip.DataContext!).RefName;
        var targetRef = ((RefLabelViewModel)targetChip.DataContext!).RefName;

        var pSource = Center(sourceChip, win);
        var pTarget = Center(targetChip, win);
        // A point just past the ~5px threshold but still on the source chip, to arm the drag.
        var pNudge = new Point(pSource.X + 8, pSource.Y);

        win.MouseDown(pSource, MouseButton.Left);
        win.MouseMove(pNudge);      // crosses threshold → drag begins, ghost raised
        Settle();
        // Past the threshold the ghost label is raised (drag in progress).
        var ghost = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("RefChipGhost"));
        Assert.True(ghost.IsVisible, "drag ghost should be visible once the pointer crosses the threshold");
        win.MouseMove(pTarget);     // hover the other chip → drop-target highlight
        Settle();

        // Mid-drag frame: ghost following the cursor + target chip highlighted.
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "label_drag_middrag.png"));

        win.MouseUp(pTarget, MouseButton.Left);
        Settle();

        // The gesture resolved to the right pair and produced the two-action flyout.
        Assert.NotNull(vm.LastDragActionPair);
        Assert.Equal(sourceRef, vm.LastDragActionPair!.Value.Source);
        Assert.Equal(targetRef, vm.LastDragActionPair!.Value.Target);
        Assert.NotNull(vm.LastDragActionMenu);
        Assert.Equal(2, vm.LastDragActionMenu!.Count);   // Merge + Rebase
        Assert.Contains(vm.LastDragActionMenu, m => m.Header!.Contains("merge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vm.LastDragActionMenu, m => m.Header!.Contains($"Rebase {sourceRef} onto {targetRef}"));
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void ReleaseOnSameLabel_ShouldNotOpenFlyout()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "one\n", "first");
        fx.CreateBranch("topic");
        fx.Checkout("topic");
        fx.CommitFile("b.txt", "two\n", "second");

        var vm = new CommitTimelineViewModel(git, fx.RepoPath);
        var view = new CommitTimelineView { DataContext = vm };
        var win = new Window { Content = view, Width = 1000, Height = 600 };
        win.Show();
        vm.LoadInitialCommits();
        Settle();

        var chip = view.GetVisualDescendants().OfType<Border>()
            .First(b => b.Classes.Contains("RefChip") && b.DataContext is RefLabelViewModel);
        var p = Center(chip, win);

        // Press-drag-release entirely over the same chip → no flyout, no side effect.
        win.MouseDown(p, MouseButton.Left);
        win.MouseMove(new Point(p.X + 8, p.Y));
        win.MouseMove(p);
        win.MouseUp(p, MouseButton.Left);
        Settle();

        Assert.Null(vm.LastDragActionPair);
        Assert.Null(vm.LastDragActionMenu);
        HarnessHygiene.Teardown(win);
    }

    private static Point Center(Visual v, Visual relativeTo)
    {
        var p = v.TranslatePoint(new Point(v.Bounds.Width / 2, v.Bounds.Height / 2), relativeTo);
        return p ?? new Point();
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
