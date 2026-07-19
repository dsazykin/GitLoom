using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using LibGit2Sharp;
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

// Renders the T-20 reflog viewer offscreen (headless Skia) so its layout/theme and the per-entry
// restore / create-branch-here affordances can be inspected without a display. Builds a realistic
// reflog (commits, a branch checkout, and a hard reset) against a fixture repo, then captures a PNG
// to artifacts_headless/ — visual review, not pass/fail.
public class ReflogRenderHarness
{
    [AvaloniaFact]
    public void Capture_ReflogWindow_WithEntries()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();

        var c1 = fx.CommitFile("app.cs", "one\n", "feat: initial commit");
        var c2 = fx.CommitFile("app.cs", "two\n", "feat: add feature");
        git.CreateBranch(fx.RepoPath, "experiment", "", checkout: true);
        fx.WriteFile("app.cs", "three\n");
        git.StageFile(fx.RepoPath, "app.cs");
        git.Commit(fx.RepoPath, "wip: risky change");
        using (var repo = new Repository(fx.RepoPath))
            Commands.Checkout(repo, repo.Branches.First(b => !b.IsRemote && b.FriendlyName != "experiment"));
        git.ResetToCommit(fx.RepoPath, c1, ResetMode.Hard); // creates a "reset: moving to" entry

        var vm = new ReflogViewModel(git, fx.RepoPath);
        var win = new ReflogWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "reflog_window.png"));

        Assert.True(vm.HasEntries);
        Assert.Contains(vm.Entries, e => e.ToSha == c1); // reset target visible
        Assert.Contains(vm.Entries, e => e.ToSha == c2); // an earlier move visible
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
