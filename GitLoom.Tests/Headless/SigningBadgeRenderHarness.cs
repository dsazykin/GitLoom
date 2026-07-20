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
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-15 commit-timeline with signature badges offscreen (headless Skia): a verified
// (green shield), a signed-but-untrusted (amber), a bad (red) and an unsigned row. PNG to
// artifacts_headless/ for the human visual pass. The badge visibility is driven purely by each
// row's SignatureStatus, so the statuses are assigned directly here for a deterministic frame
// (the gpg round trip itself is proven in GitServiceSigningTests).
public class SigningBadgeRenderHarness
{
    [AvaloniaFact]
    public void Capture_CommitTimeline_WithSignatureBadges()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var t0 = new DateTimeOffset(2024, 5, 1, 9, 0, 0, TimeSpan.Zero);
        fx.CommitFile("a.txt", "one\n", "verified: release build", "Ada Lovelace", "ada@gitloom.local", t0);
        fx.CommitFile("a.txt", "two\n", "signed, unknown key validity", "Grace Hopper", "grace@gitloom.local", t0.AddDays(1));
        fx.CommitFile("a.txt", "three\n", "tampered: bad signature", "Mallory", "m@evil.example", t0.AddDays(2));
        fx.CommitFile("a.txt", "four\n", "plain unsigned commit", "Linus Torvalds", "linus@gitloom.local", t0.AddDays(3));

        var vm = new CommitTimelineViewModel(git, fx.RepoPath);
        vm.LoadInitialCommits();
        Pump(() => vm.Commits.Count >= 4);

        // Assign one of each status so all three badge variants + the no-badge case render.
        var statuses = new[]
        {
            SignatureStatus.Good, SignatureStatus.UnknownValidity, SignatureStatus.Bad, SignatureStatus.None,
        };
        for (int i = 0; i < vm.Commits.Count && i < statuses.Length; i++)
        {
            vm.Commits[i].SignatureStatus = statuses[i];
            vm.Commits[i].SignatureSigner = vm.Commits[i].Commit.AuthorName;
        }

        var host = new Window
        {
            Width = 900,
            Height = 320,
            Content = new CommitTimelineView { DataContext = vm },
            DataContext = vm,
        };
        host.Show();
        Settle();

        host.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "signing_badges.png"));

        // The three signed rows show a badge; the unsigned row does not.
        Assert.True(vm.Commits[0].IsSignatureVerified);
        Assert.True(vm.Commits[1].IsSignatureUntrusted);
        Assert.True(vm.Commits[2].IsSignatureBad);
        Assert.True(vm.Commits[0].HasSignatureBadge);
        Assert.False(vm.Commits[3].HasSignatureBadge);
        HarnessHygiene.Teardown(host);
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static void Pump(Func<bool> until)
    {
        for (int i = 0; i < 250 && !until(); i++)
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
