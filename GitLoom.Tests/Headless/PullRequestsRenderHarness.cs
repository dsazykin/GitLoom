using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-23 Pull Requests panel offscreen (headless Skia) in its two key states so the
// layout/theme and the per-PR Merge/Close/Open affordances can be inspected without a display:
//   1. populated — an open-PR list (incl. a draft badge) served by a canned service;
//   2. unsupported/no-token — the graceful sign-in affordance instead of an error.
// Captures a PNG per state to artifacts_headless/ (visual review, not pass/fail).
public class PullRequestsRenderHarness
{
    private static FakeGitService Git() => new()
    {
        GetHeadStateImpl = _ => new GitHeadState { CurrentBranchName = "feature/T-23-pr-integration", Sha = "abc123" },
        GetBranchesImpl = _ => new[]
        {
            new GitBranchItem { FriendlyName = "main", IsRemote = false },
            new GitBranchItem { FriendlyName = "feature/T-23-pr-integration", IsRemote = false },
        },
        GetRecentCommitsImpl = (_, _, _) => new[] { new GitCommitItem { MessageShort = "feat: pull-request integration" } },
    };

    [AvaloniaFact]
    public void Capture_PullRequests_Populated()
    {
        var pr = new FakePullRequestService
        {
            IsSupportedImpl = _ => true,
            ListImpl = (_, _) => new[]
            {
                new PullRequestItem { Number = 42, Title = "Add multi-host PR integration", Author = "danielsazykin",
                    SourceBranch = "feature/T-23-pr-integration", TargetBranch = "main", State = PullRequestState.Open,
                    Url = "https://github.com/octocat/hello-world/pull/42" },
                new PullRequestItem { Number = 41, Title = "Command palette polish", Author = "octocat",
                    SourceBranch = "palette-polish", TargetBranch = "main", IsDraft = true, State = PullRequestState.Draft,
                    Url = "https://github.com/octocat/hello-world/pull/41" },
            },
        };

        var vm = new PullRequestsViewModel(pr, Git(), "/repo");
        var win = new PullRequestsWindow { DataContext = vm };
        win.Show();
        vm.RefreshListCommand.Execute(null);
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "pull_requests_populated.png"));

        Assert.True(vm.IsSupported);
        Assert.Equal(2, vm.PullRequests.Count);
        Assert.Contains(vm.PullRequests, r => r.ShowDraftBadge);
    }

    [AvaloniaFact]
    public void Capture_PullRequests_ReviewPanel()
    {
        var pr = new FakePullRequestService
        {
            IsSupportedImpl = _ => true,
            ListImpl = (_, _) => new[]
            {
                new PullRequestItem { Number = 42, Title = "In-app PR review", Author = "danielsazykin",
                    SourceBranch = "feature/T-25-pr-review", TargetBranch = "main", State = PullRequestState.Open,
                    Url = "https://github.com/octocat/hello-world/pull/42" },
            },
            ReviewsImpl = (_, _) => new[]
            {
                new PullRequestReview { Id = 1, Author = "octocat", State = ReviewState.Approved,
                    Body = "Reads clean and reuses the shared transport — approving." },
                new PullRequestReview { Id = 2, Author = "hubot", State = ReviewState.ChangesRequested,
                    Body = "Please cover the outdated-comment path with a test before this lands." },
                new PullRequestReview { Id = 3, Author = "monalisa", State = ReviewState.Commented,
                    Body = "One nit inline; no blockers." },
            },
            ReviewCommentsImpl = (_, _) => new[]
            {
                new ReviewComment { Id = 10, Author = "hubot", Path = "GitHubPullRequestProvider.cs", Line = 84,
                    DiffHunk = "@@ -80,6 +80,10 @@\n+        var url = ...reviews?per_page=100", Body = "Good call adding per_page here." },
                new ReviewComment { Id = 11, Author = "octocat", Path = "GitHubPullRequestProvider.cs", Line = 92,
                    DiffHunk = "@@ -88,4 +88,8 @@\n+        return dtos.Select(MapReviewComment).ToList();", Body = "Mapping stays in the provider — nice." },
                new ReviewComment { Id = 12, Author = "hubot", Path = "T-25-pr-review.md", Line = null,
                    DiffHunk = "@@ -10,3 +10,3 @@\n-old\n+new", Body = "This one is on an outdated diff." },
            },
        };

        var vm = new PullRequestsViewModel(pr, Git(), "/repo");
        // Taller than the default so the whole review panel — reviews, threads, and the submit affordance —
        // fits the captured frame for visual review.
        var win = new PullRequestsWindow { DataContext = vm, Height = 1040 };
        win.Show();
        vm.RefreshListCommand.Execute(null);
        Settle();
        vm.PullRequests[0].ReviewCommand.Execute(null);
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "pull_requests_review.png"));

        Assert.True(vm.IsReviewOpen);
        Assert.Equal(3, vm.Reviews.Count);
        Assert.Equal(2, vm.CommentThreads.Count);            // grouped by path
        Assert.Contains(vm.CommentThreads.SelectMany(t => t.Comments), c => c.IsOutdated);
    }

    [AvaloniaFact]
    public void Capture_PullRequests_Unsupported()
    {
        var pr = new FakePullRequestService { IsSupportedImpl = _ => false };
        var vm = new PullRequestsViewModel(pr, Git(), "/repo");
        var win = new PullRequestsWindow { DataContext = vm };
        win.Show();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "pull_requests_unsupported.png"));

        Assert.False(vm.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnsupportedHint));
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
