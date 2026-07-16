using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-08 interactive-rebase dialog offscreen (headless Skia) so its layout —
// the action dropdowns, the SHA column, and the squash/fixup fold rail — can be inspected
// without a display. Captures a PNG to artifacts_headless/ for visual review.
public class InteractiveRebaseRenderHarness
{
    [AvaloniaFact]
    public void Capture_InteractiveRebasePlan_WithFoldRail()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var baseSha = fx.CommitFile("base.txt", "b\n", "base");
        fx.CommitFile("a.txt", "a\n", "add feature scaffold");
        fx.CommitFile("b.txt", "b\n", "fix typo in scaffold");

        var vm = new InteractiveRebaseViewModel(new InteractiveRebaseService(), fx.RepoPath, baseSha, null, git);
        vm.LoadPlan();

        // Fold the last commit into the first so the grouping rail is exercised.
        vm.SelectedItem = vm.Plan.Last();
        vm.SetActionCommand.Execute(RebaseAction.Squash);
        vm.Plan.Last().NewMessage = "add feature scaffold (squashed)";

        var win = new InteractiveRebaseWindow { DataContext = vm };
        win.Show();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "interactive_rebase_plan.png"));

        // The squash row folds; the first row remains a valid Pick (guard holds).
        Assert.True(vm.Plan.Last().IsFolded);
        Assert.Equal(RebaseAction.Pick, vm.Plan.First().Action);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void SetAction_FirstItemSquash_ShouldDowngradeToPick()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var baseSha = fx.CommitFile("base.txt", "b\n", "base");
        fx.CommitFile("a.txt", "a\n", "c1");
        fx.CommitFile("b.txt", "b\n", "c2");

        var vm = new InteractiveRebaseViewModel(new InteractiveRebaseService(), fx.RepoPath, baseSha, null, git);
        vm.LoadPlan();

        // Pressing "S" on the first row must not produce an illegal first-item squash.
        vm.SelectedItem = vm.Plan.First();
        vm.SetActionCommand.Execute(RebaseAction.Squash);

        Assert.Equal(RebaseAction.Pick, vm.Plan.First().Action);
        Assert.False(vm.Plan.First().IsFolded);
    }

    private static void Settle()
    {
        for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
