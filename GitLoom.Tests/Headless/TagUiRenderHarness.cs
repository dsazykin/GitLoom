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
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-05 tag UI offscreen (headless Skia) so its layout/theme can be inspected
// without a display. Captures PNGs to artifacts_headless/ — visual review, not pass/fail.
public class TagUiRenderHarness
{
    [AvaloniaFact]
    public void Capture_CreateTagDialog()
    {
        var vm = new CreateTagDialogViewModel
        {
            TargetSha = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
            TagName = "v1.0.0",
            IsAnnotated = true,
            Message = "Ship release 1.0"
        };
        var dlg = new CreateTagDialog { DataContext = vm };
        dlg.Show();

        Settle();
        var frame = dlg.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir(), "create_tag_dialog.png"));
        HarnessHygiene.Teardown(dlg);
    }

    [AvaloniaFact]
    public void Capture_TimelineTagChips()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var sha = fx.CommitFile("a.txt", "hello\n", "init");
        git.CreateTag(fx.RepoPath, "v1.0.0", sha, "release one");   // annotated
        git.CreateTag(fx.RepoPath, "nightly", sha, null);           // lightweight

        var vm = new CommitTimelineViewModel(git, fx.RepoPath, null);
        var view = new CommitTimelineView { DataContext = vm };
        var win = new Window { Content = view, Width = 1100, Height = 480 };
        win.Show();

        vm.LoadInitialCommits(); // the real view triggers this on load
        Pump(() => vm.Commits.Count > 0);
        vm.SelectedCommit = vm.Commits.FirstOrDefault();
        Pump(() => vm.SelectedCommitTags.Count >= 2);
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "timeline_tag_chips.png"));
        Assert.Contains("v1.0.0", vm.SelectedCommitTags);
        Assert.Contains("nightly", vm.SelectedCommitTags);

        // Tags category is present in the branch browser.
        Assert.Contains(vm.BranchBrowser.BranchCategories, c => c.CategoryName == "Tags");
        var tags = vm.BranchBrowser.BranchCategories.First(c => c.CategoryName == "Tags");
        Assert.Equal(2, tags.Branches.Count);
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static void Pump(Func<bool> until)
    {
        for (int i = 0; i < 150 && !until(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
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
