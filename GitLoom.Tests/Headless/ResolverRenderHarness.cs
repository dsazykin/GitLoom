using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the conflict resolver offscreen (headless Skia) against a real conflicted repo and
// saves a PNG, so the resolver's layout can be inspected without a display. Not a pass/fail
// assertion harness — it captures a frame for visual review.
public class ResolverRenderHarness
{
    [AvaloniaFact]
    public void Capture_TwoConflictFile()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();

        fx.CommitFile("f.txt", "a\nX\nb\n", "base");
        fx.CreateBranch("theirs");
        fx.CreateBranch("ours");
        fx.Checkout("theirs");
        fx.CommitFile("f.txt", "a\nTHEIRS\nb\nTHEIRS!\n", "theirs");
        fx.Checkout("ours");
        fx.CommitFile("f.txt", "a\nOURS\nb\nOURS!\n", "ours");
        Assert.Throws<MergeConflictException>(() => git.Merge(fx.RepoPath, "theirs"));

        var vm = new ConflictResolverWindowViewModel(git, new MergeDiffService(), fx.RepoPath, "f.txt", true, true);
        var win = new ConflictResolverWindow { DataContext = vm };
        win.Show();

        Pump(() => !vm.IsLoading && vm.Chunks.Count > 0);
        // Let layout + a render pass settle.
        for (int i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(ArtifactsDir(), "resolver_two_conflicts.png"));

        // Resolve: take ours on the first conflict, theirs on the second, then re-capture so the
        // Result fills and the bands turn "resolved".
        var conflicts = System.Linq.Enumerable.Where(vm.Chunks, c => c.IsConflict)
            .ToList();
        if (conflicts.Count >= 1) conflicts[0].ForceOurs();
        if (conflicts.Count >= 2) conflicts[1].ForceTheirs();
        for (int i = 0; i < 5; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }

        var frame2 = win.CaptureRenderedFrame();
        frame2?.Save(Path.Combine(ArtifactsDir(), "resolver_resolved.png"));
        Assert.True(vm.IsFullyResolved);
    }

    private static void Pump(Func<bool> until)
    {
        for (int i = 0; i < 100 && !until(); i++)
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
