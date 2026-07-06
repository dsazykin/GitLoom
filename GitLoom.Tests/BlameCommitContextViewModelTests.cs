using System.Collections.Generic;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-32 (VM gating) — the blame → PR popover routing: a single PR jumps directly, several PRs reveal a
/// chooser, no PR disables the jump, and PR/issue rows route the right model out through the injected
/// sinks (the seam the host wires to the PR / Issues panel or the browser). Also covers
/// <see cref="BlameViewModel.ShowCommitContextAsync"/> gating (unsupported → inert; browser fallback URL).
/// </summary>
public class BlameCommitContextViewModelTests
{
    private static PullRequestItem Pr(int n, string url = "") => new()
    {
        Number = n,
        Title = $"PR {n}",
        Url = url.Length == 0 ? $"https://github.com/octocat/hello-world/pull/{n}" : url,
        State = PullRequestState.Merged,
    };

    private static BlameCommitContextViewModel Build(
        CommitContextResult result,
        out List<PullRequestItem> openedPrs,
        out List<LinkedIssueRef> openedIssues)
    {
        var prs = new List<PullRequestItem>();
        var issues = new List<LinkedIssueRef>();
        openedPrs = prs;
        openedIssues = issues;
        return new BlameCommitContextViewModel(result, prs.Add, issues.Add);
    }

    // ---- Pull-request gating -------------------------------------------------------------------

    [Fact]
    public void SinglePr_GoToPullRequest_RoutesDirectly_NoChooser()
    {
        var vm = Build(new CommitContextResult { PullRequests = new[] { Pr(42) } }, out var opened, out _);

        Assert.True(vm.HasSinglePullRequest);
        Assert.True(vm.GoToPullRequestCommand.CanExecute(null));
        vm.GoToPullRequestCommand.Execute(null);

        Assert.False(vm.IsChoosingPullRequest);
        var pr = Assert.Single(opened);
        Assert.Equal(42, pr.Number);
    }

    [Fact]
    public void MultiplePrs_GoToPullRequest_RevealsChooser_ThenRowRoutes()
    {
        var vm = Build(new CommitContextResult { PullRequests = new[] { Pr(42), Pr(55) } }, out var opened, out _);

        Assert.True(vm.HasMultiplePullRequests);
        vm.GoToPullRequestCommand.Execute(null);

        // Several PRs → chooser revealed, nothing routed yet.
        Assert.True(vm.IsChoosingPullRequest);
        Assert.Empty(opened);

        // Picking a row routes that PR and closes the chooser.
        vm.PullRequests[1].OpenCommand.Execute(null);
        Assert.False(vm.IsChoosingPullRequest);
        Assert.Equal(55, Assert.Single(opened).Number);
    }

    [Fact]
    public void NoPr_JumpDisabled_AndHasNothing_WhenNoIssuesEither()
    {
        var vm = Build(new CommitContextResult(), out _, out _);

        Assert.False(vm.HasPullRequests);
        Assert.False(vm.GoToPullRequestCommand.CanExecute(null));
        Assert.True(vm.HasNothing);
    }

    [Fact]
    public void PrRow_Open_RoutesTheModel_WithUrl()
    {
        var vm = Build(new CommitContextResult { PullRequests = new[] { Pr(7, "https://example.test/pr/7") } },
            out var opened, out _);

        vm.PullRequests[0].OpenCommand.Execute(null);
        Assert.Equal("https://example.test/pr/7", Assert.Single(opened).Url);
    }

    // ---- Linked-issue gating -------------------------------------------------------------------

    [Fact]
    public void SingleIssue_GoToLinkedIssue_Routes()
    {
        var result = new CommitContextResult
        {
            LinkedIssues = new[] { new LinkedIssueRef { Number = 12, RepoFullName = "octocat/hello-world" } },
        };
        var vm = Build(result, out _, out var openedIssues);

        Assert.True(vm.HasLinkedIssues);
        Assert.True(vm.GoToLinkedIssueCommand.CanExecute(null));
        vm.GoToLinkedIssueCommand.Execute(null);
        Assert.Equal(12, Assert.Single(openedIssues).Number);
    }

    [Fact]
    public void MultipleIssues_TopLevelJumpDisabled_ButRowRoutes()
    {
        var result = new CommitContextResult
        {
            LinkedIssues = new[]
            {
                new LinkedIssueRef { Number = 12, RepoFullName = "octocat/hello-world" },
                new LinkedIssueRef { Number = 3, RepoFullName = "octocat/spec" },
            },
        };
        var vm = Build(result, out _, out var openedIssues);

        Assert.False(vm.GoToLinkedIssueCommand.CanExecute(null)); // ambiguous — pick a row
        vm.LinkedIssues[1].OpenCommand.Execute(null);
        var issue = Assert.Single(openedIssues);
        Assert.Equal(3, issue.Number);
        Assert.Equal("octocat/spec", issue.RepoFullName);
    }

    [Fact]
    public void IssueRow_DisplayText_BareVsCrossRepo()
    {
        var result = new CommitContextResult
        {
            LinkedIssues = new[]
            {
                new LinkedIssueRef { Number = 12, RepoFullName = "octocat/hello-world" },
                new LinkedIssueRef { Number = 3, RepoFullName = "" },
            },
        };
        var vm = Build(result, out _, out _);
        Assert.Equal("octocat/hello-world#12", vm.LinkedIssues[0].DisplayText);
        Assert.Equal("#3", vm.LinkedIssues[1].DisplayText);
    }

    // ---- BlameViewModel host wiring ------------------------------------------------------------

    [Fact]
    public async Task BlameVm_ShowContext_Unsupported_IsInert()
    {
        var fakeGit = new FakeGitService();
        var ctx = new FakeCommitContextService { IsSupportedImpl = _ => false };
        var vm = new BlameViewModel(fakeGit, "/repo", ctx);

        Assert.False(vm.IsCommitContextSupported);
        await vm.ShowCommitContextAsync("deadbeef");
        Assert.Null(vm.LineContext); // never resolved
    }

    [Fact]
    public async Task BlameVm_ShowContext_Supported_OpensPopover_AndRoutesToSink()
    {
        var routed = new List<PullRequestItem>();
        var fakeGit = new FakeGitService();
        var ctx = new FakeCommitContextService
        {
            IsSupportedImpl = _ => true,
            GetForCommitImpl = (_, sha) => new CommitContextResult
            {
                Sha = sha,
                PullRequests = new[] { Pr(42) },
            },
        };
        var vm = new BlameViewModel(fakeGit, "/repo", ctx, openPullRequest: routed.Add);

        Assert.True(vm.IsCommitContextSupported);
        await vm.ShowCommitContextAsync("deadbeef");

        Assert.NotNull(vm.LineContext);
        Assert.True(vm.LineContext!.HasSinglePullRequest);
        vm.LineContext.GoToPullRequestCommand.Execute(null);
        Assert.Equal(42, Assert.Single(routed).Number);

        vm.CloseLineContext();
        Assert.Null(vm.LineContext);
    }
}
