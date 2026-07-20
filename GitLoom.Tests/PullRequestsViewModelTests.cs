using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using GitLoom.App.ViewModels;
using Mainguard.Git.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-23 (ViewModel) — gating/state of <see cref="PullRequestsViewModel"/> over fakes: Create is
/// disabled (with a hint) on a detached/unborn HEAD, <see cref="PullRequestsViewModel.IsBusy"/> gates
/// every command, the unsupported/no-token state degrades gracefully, and the list marshals results
/// onto the observable collection. No live network.
/// </summary>
public class PullRequestsViewModelTests
{
    private static FakeGitService GitOn(GitHeadState head, params string[] localBranches)
    {
        var branches = localBranches.Select(b => new GitBranchItem { FriendlyName = b, IsRemote = false }).ToList();
        return new FakeGitService
        {
            GetHeadStateImpl = _ => head,
            GetBranchesImpl = _ => branches,
            GetRecentCommitsImpl = (_, _, _) => new[] { new GitCommitItem { MessageShort = "feat: last subject" } },
        };
    }

    private static FakePullRequestService Pr(bool supported = true) =>
        new() { IsSupportedImpl = _ => supported };

    private static readonly GitHeadState Attached = new() { CurrentBranchName = "feature", Sha = "abc" };
    private static readonly GitHeadState Detached = new() { IsDetached = true, Sha = "abc" };
    private static readonly GitHeadState Unborn = new() { IsUnborn = true };

    [Fact]
    public void AttachedHead_PrefillsCreateForm_AndEnablesCreate()
    {
        var vm = new PullRequestsViewModel(Pr(), GitOn(Attached, "main", "feature"), "/repo");

        Assert.True(vm.IsSupported);
        Assert.True(vm.CanCreate);
        Assert.Equal("feature", vm.NewSourceBranch);
        Assert.Equal("main", vm.NewTargetBranch);
        Assert.Equal("feat: last subject", vm.NewTitle);
        Assert.True(vm.BeginCreateCommand.CanExecute(null));
        Assert.Null(vm.CreateDisabledHint);
    }

    [Fact]
    public void DetachedHead_DisablesCreate_WithHint()
    {
        var vm = new PullRequestsViewModel(Pr(), GitOn(Detached, "main"), "/repo");

        Assert.False(vm.CanCreate);
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.SubmitCreateCommand.CanExecute(null));
        Assert.NotNull(vm.CreateDisabledHint);
    }

    [Fact]
    public void UnbornHead_DisablesCreate_WithHint()
    {
        var vm = new PullRequestsViewModel(Pr(), GitOn(Unborn), "/repo");

        Assert.False(vm.CanCreate);
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.Contains("commit", vm.CreateDisabledHint!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsBusy_GatesAllCommands()
    {
        var vm = new PullRequestsViewModel(Pr(), GitOn(Attached, "main", "feature"), "/repo");
        Assert.True(vm.RefreshListCommand.CanExecute(null));

        vm.IsBusy = true;

        Assert.False(vm.RefreshListCommand.CanExecute(null));
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.SubmitCreateCommand.CanExecute(null));
    }

    [Fact]
    public void UnsupportedHost_ShowsAffordance_AndDisablesList()
    {
        var vm = new PullRequestsViewModel(Pr(supported: false), GitOn(Attached, "main", "feature"), "/repo");

        Assert.False(vm.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnsupportedHint));
        Assert.False(vm.RefreshListCommand.CanExecute(null));
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
    }

    [Fact]
    public void SubmitCreate_DisabledWhenTitleBlank()
    {
        var vm = new PullRequestsViewModel(Pr(), GitOn(Attached, "main", "feature"), "/repo")
        {
            NewTitle = "",
        };
        Assert.False(vm.SubmitCreateCommand.CanExecute(null));

        vm.NewTitle = "A title";
        Assert.True(vm.SubmitCreateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RefreshList_MarshalsResultsIntoCollection()
    {
        var pr = Pr();
        pr.ListImpl = (_, _) => new[]
        {
            new PullRequestItem { Number = 42, Title = "A", SourceBranch = "f", TargetBranch = "main", Author = "me" },
            new PullRequestItem { Number = 41, Title = "B", IsDraft = true, State = PullRequestState.Draft },
        };
        var vm = new PullRequestsViewModel(pr, GitOn(Attached, "main", "feature"), "/repo");

        await vm.RefreshListCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.PullRequests.Count);
        Assert.False(vm.IsEmpty);
        Assert.Contains(vm.PullRequests, r => r.Number == 42 && r.BranchFlow == "f → main");
        Assert.Contains(vm.PullRequests, r => r.ShowDraftBadge);
    }

    [AvaloniaFact]
    public async Task Merge_RoutesThroughService_AndRefreshes()
    {
        int? merged = null;
        var pr = Pr();
        pr.MergeImpl = (_, number, _) => { merged = number; return new PullRequestItem { Number = number, State = PullRequestState.Merged }; };
        pr.ListImpl = (_, _) => System.Array.Empty<PullRequestItem>();
        var vm = new PullRequestsViewModel(pr, GitOn(Attached, "main", "feature"), "/repo");

        var row = new PullRequestRowViewModel(new PullRequestItem { Number = 7 }, vm) { SelectedMergeMethod = PullRequestMergeMethod.Squash };
        await row.MergeCommand.ExecuteAsync(null);

        Assert.Equal(7, merged);
        Assert.Null(vm.ErrorMessage);
    }

    // ---- Review (T-25) ------------------------------------------------------------------------

    [AvaloniaFact]
    public async Task Review_LoadsReviews_AndGroupsCommentThreadsByPath()
    {
        var pr = Pr();
        pr.ReviewsImpl = (_, _) => new[]
        {
            new PullRequestReview { Id = 1, Author = "octocat", State = ReviewState.Approved, Body = "LGTM" },
            new PullRequestReview { Id = 2, Author = "hubot", State = ReviewState.ChangesRequested, Body = "fix" },
        };
        pr.ReviewCommentsImpl = (_, _) => new[]
        {
            new ReviewComment { Id = 10, Author = "hubot", Path = "b.cs", Line = 5, Body = "nit" },
            new ReviewComment { Id = 11, Author = "octocat", Path = "a.cs", Line = 9, Body = "ok" },
            new ReviewComment { Id = 12, Author = "hubot", Path = "a.cs", Line = null, Body = "outdated" },
        };
        var vm = new PullRequestsViewModel(pr, GitOn(Attached, "main", "feature"), "/repo");

        var row = new PullRequestRowViewModel(new PullRequestItem { Number = 42 }, vm);
        await row.ReviewCommand.ExecuteAsync(null);

        Assert.True(vm.IsReviewOpen);
        Assert.Same(row, vm.SelectedReviewPr);
        Assert.True(vm.HasReviews);
        Assert.Equal(2, vm.Reviews.Count);

        // Two threads (a.cs, b.cs), ordered by path; a.cs has the current + the outdated (null line) comment.
        Assert.True(vm.HasCommentThreads);
        Assert.Equal(2, vm.CommentThreads.Count);
        Assert.Equal("a.cs", vm.CommentThreads[0].Path);
        Assert.Equal(2, vm.CommentThreads[0].Comments.Count);
        Assert.Contains(vm.CommentThreads[0].Comments, c => c.IsOutdated && c.LineText == "outdated");
        Assert.Equal("b.cs", vm.CommentThreads[1].Path);
    }

    [Fact]
    public void ReviewRow_VerdictFlags_MapState()
    {
        var approved = new ReviewRowViewModel(new PullRequestReview { State = ReviewState.Approved });
        Assert.True(approved.IsApproved);
        Assert.False(approved.IsChangesRequested);
        Assert.False(approved.IsNeutral);
        Assert.Equal("Approved", approved.VerdictText);

        var changes = new ReviewRowViewModel(new PullRequestReview { State = ReviewState.ChangesRequested });
        Assert.True(changes.IsChangesRequested);
        Assert.Equal("Changes requested", changes.VerdictText);

        var commented = new ReviewRowViewModel(new PullRequestReview { State = ReviewState.Commented });
        Assert.True(commented.IsNeutral);
        Assert.Equal("Commented", commented.VerdictText);
    }

    [AvaloniaFact]
    public async Task SubmitReview_RequiresBody_ExceptForApprove()
    {
        var pr = Pr();
        var vm = new PullRequestsViewModel(pr, GitOn(Attached, "main", "feature"), "/repo");
        await new PullRequestRowViewModel(new PullRequestItem { Number = 42 }, vm).ReviewCommand.ExecuteAsync(null);

        // Comment verdict with a blank body → disabled.
        vm.SubmitVerdict = ReviewVerdict.Comment;
        vm.ReviewBody = "";
        Assert.False(vm.SubmitReviewCommand.CanExecute(null));

        // Approve tolerates an empty body.
        vm.SubmitVerdict = ReviewVerdict.Approve;
        Assert.True(vm.SubmitReviewCommand.CanExecute(null));

        // Request-changes needs a body.
        vm.SubmitVerdict = ReviewVerdict.RequestChanges;
        Assert.False(vm.SubmitReviewCommand.CanExecute(null));
        vm.ReviewBody = "please change X";
        Assert.True(vm.SubmitReviewCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SubmitReview_IsBusy_GatesAndDisables()
    {
        var vm = new PullRequestsViewModel(Pr(), GitOn(Attached, "main", "feature"), "/repo");
        await new PullRequestRowViewModel(new PullRequestItem { Number = 42 }, vm).ReviewCommand.ExecuteAsync(null);
        vm.SubmitVerdict = ReviewVerdict.Approve;
        Assert.True(vm.SubmitReviewCommand.CanExecute(null));

        vm.IsBusy = true;
        Assert.False(vm.SubmitReviewCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task SubmitReview_RoutesThroughService_WithVerdictAndBody()
    {
        SubmitReview? captured = null;
        var pr = Pr();
        pr.SubmitReviewImpl = (_, _, review) => { captured = review; return new PullRequestReview { Id = 9, State = ReviewState.ChangesRequested }; };
        var vm = new PullRequestsViewModel(pr, GitOn(Attached, "main", "feature"), "/repo");
        await new PullRequestRowViewModel(new PullRequestItem { Number = 42 }, vm).ReviewCommand.ExecuteAsync(null);

        vm.SubmitVerdict = ReviewVerdict.RequestChanges;
        vm.ReviewBody = "needs a test";
        await vm.SubmitReviewCommand.ExecuteAsync(null);

        Assert.NotNull(captured);
        Assert.Equal(ReviewVerdict.RequestChanges, captured!.Verdict);
        Assert.Equal("needs a test", captured.Body);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal("", vm.ReviewBody); // cleared after a successful submit
    }

    [AvaloniaFact]
    public void UnsupportedHost_DisablesReviewSubmit()
    {
        var vm = new PullRequestsViewModel(Pr(supported: false), GitOn(Attached, "main", "feature"), "/repo");
        Assert.False(vm.SubmitReviewCommand.CanExecute(null));
    }

    // ---- Check out locally (T-29) -------------------------------------------------------------

    [AvaloniaFact]
    public async Task CheckoutLocally_Success_OffersOpenWorktree_AndRoutesOpen()
    {
        var git = GitOn(Attached, "main", "feature");
        git.CheckoutPullRequestWorktreeImpl = (_, _, _, target) => target;   // echoes the created path
        string? opened = null;
        var vm = new PullRequestsViewModel(Pr(), git, "/repo",
            pickWorktreeFolder: _ => Task.FromResult<string?>("/tmp/repo-pr-42"),
            openWorktree: p => opened = p);
        var row = new PullRequestRowViewModel(new PullRequestItem { Number = 42 }, vm);

        await row.CheckoutLocallyCommand.ExecuteAsync(null);

        Assert.True(vm.CanOpenWorktree);
        Assert.Equal("/tmp/repo-pr-42", vm.LastCheckoutPath);
        Assert.Equal(42, vm.LastCheckoutPrNumber);
        Assert.Null(vm.ErrorMessage);

        Assert.True(vm.OpenWorktreeCommand.CanExecute(null));
        vm.OpenWorktreeCommand.Execute(null);
        Assert.Equal("/tmp/repo-pr-42", opened);
    }

    [AvaloniaFact]
    public async Task CheckoutLocally_FolderPickCancelled_DoesNothing()
    {
        var git = GitOn(Attached, "main", "feature");
        var called = false;
        git.CheckoutPullRequestWorktreeImpl = (_, _, _, _) => { called = true; return "x"; };
        var vm = new PullRequestsViewModel(Pr(), git, "/repo",
            pickWorktreeFolder: _ => Task.FromResult<string?>(null));   // user cancels
        var row = new PullRequestRowViewModel(new PullRequestItem { Number = 7 }, vm);

        await row.CheckoutLocallyCommand.ExecuteAsync(null);

        Assert.False(called);
        Assert.False(vm.CanOpenWorktree);
        Assert.False(vm.OpenWorktreeCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task CheckoutLocally_TypedError_SurfacesAndKeepsWorktreeClosed()
    {
        var git = GitOn(Attached, "main", "feature");
        git.CheckoutPullRequestWorktreeImpl = (_, _, _, _) =>
            throw new Mainguard.Git.Exceptions.GitOperationException("target not empty");
        var vm = new PullRequestsViewModel(Pr(), git, "/repo",
            pickWorktreeFolder: _ => Task.FromResult<string?>("/tmp/repo-pr-9"));
        var row = new PullRequestRowViewModel(new PullRequestItem { Number = 9 }, vm);

        await row.CheckoutLocallyCommand.ExecuteAsync(null);

        Assert.Equal("target not empty", vm.ErrorMessage);
        Assert.False(vm.CanOpenWorktree);
    }

    [AvaloniaFact]
    public async Task CheckoutLocally_WhileBusy_IsNoOp()
    {
        var git = GitOn(Attached, "main", "feature");
        var called = false;
        git.CheckoutPullRequestWorktreeImpl = (_, _, _, _) => { called = true; return "x"; };
        var vm = new PullRequestsViewModel(Pr(), git, "/repo",
            pickWorktreeFolder: _ => Task.FromResult<string?>("/tmp/x"));
        vm.IsBusy = true;
        var row = new PullRequestRowViewModel(new PullRequestItem { Number = 1 }, vm);

        await row.CheckoutLocallyCommand.ExecuteAsync(null);

        Assert.False(called);
    }
}
