using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;
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
}
