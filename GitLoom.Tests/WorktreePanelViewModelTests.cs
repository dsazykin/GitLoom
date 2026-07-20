using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using Mainguard.Git.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-21 (worktree VM) — <see cref="WorktreePanelViewModel"/> validation over a canned
/// <see cref="FakeGitService"/>: creating a worktree on a branch already checked out in another
/// worktree is disallowed (<see cref="WorktreePanelViewModel.CanCreate"/> false), while a free branch
/// or a new branch is allowed and drives the right <c>AddWorktree</c> call.
/// </summary>
public class WorktreePanelViewModelTests
{
    private static FakeGitService FakeWith(params (string branch, bool detached, bool main)[] worktrees)
    {
        var wts = worktrees.Select(w => new WorktreeItem
        {
            Path = "/wt/" + (w.branch ?? "detached"),
            Branch = w.detached ? null : w.branch,
            IsDetached = w.detached,
            IsMain = w.main,
            HeadSha = "abcdef1234567890",
        }).ToList();

        return new FakeGitService
        {
            ListWorktreesImpl = _ => wts,
            GetBranchesImpl = _ => new[]
            {
                new GitBranchItem { FriendlyName = "main", IsRemote = false },
                new GitBranchItem { FriendlyName = "feature", IsRemote = false },
                new GitBranchItem { FriendlyName = "origin/main", IsRemote = true },
            },
        };
    }

    [Fact]
    public void Ctor_ShouldLoadWorktrees_AndLocalBranchesOnly()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo");

        Assert.Single(vm.Worktrees);
        Assert.Equal(new[] { "main", "feature" }, vm.Branches);
    }

    [Fact]
    public void CanCreate_WhenSelectedBranchAlreadyCheckedOut_ShouldBeFalse()
    {
        // "main" is checked out in the main worktree; "feature" is free.
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            NewWorktreePath = "../wt",
            SelectedBranch = "main",
        };

        Assert.True(vm.SelectedBranchIsCheckedOut);
        Assert.False(vm.CanCreate); // git forbids a second checkout of the same branch
    }

    [Fact]
    public void CanCreate_WithFreeBranchAndPath_ShouldBeTrue()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            NewWorktreePath = "../wt",
            SelectedBranch = "feature",
        };

        Assert.False(vm.SelectedBranchIsCheckedOut);
        Assert.True(vm.CanCreate);
    }

    [Fact]
    public void CanCreate_WithoutPath_ShouldBeFalse()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            SelectedBranch = "feature",
        };

        Assert.False(vm.CanCreate);
    }

    [Fact]
    public void CanCreate_NewBranchMode_ShouldValidateNameNotCheckout()
    {
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true)), "/repo")
        {
            NewWorktreePath = "../wt",
            CreateBranch = true,
        };
        Assert.False(vm.CanCreate); // no name yet

        vm.NewBranchName = "brand-new";
        Assert.True(vm.CanCreate);
        Assert.False(vm.SelectedBranchIsCheckedOut); // checkout rule doesn't apply to a new branch
    }

    [Fact]
    public async Task Create_WithNewBranch_ShouldCallAddWorktreeWithCreateFlag()
    {
        (string repo, string path, string branch, bool create)? call = null;
        var fake = FakeWith(("main", false, true));
        fake.AddWorktreeImpl = (r, p, b, c) => call = (r, p, b, c);

        var vm = new WorktreePanelViewModel(fake, "/repo")
        {
            NewWorktreePath = "../feat-wt",
            CreateBranch = true,
            NewBranchName = "feat",
        };

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(call);
        Assert.Equal("/repo", call!.Value.repo);
        Assert.Equal("../feat-wt", call.Value.path);
        Assert.Equal("feat", call.Value.branch);
        Assert.True(call.Value.create);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Create_FromExistingBranch_ShouldCallAddWorktreeWithoutCreateFlag()
    {
        (string repo, string path, string branch, bool create)? call = null;
        var fake = FakeWith(("main", false, true));
        fake.AddWorktreeImpl = (r, p, b, c) => call = (r, p, b, c);

        var vm = new WorktreePanelViewModel(fake, "/repo")
        {
            NewWorktreePath = "../feature-wt",
            SelectedBranch = "feature",
        };

        await vm.CreateCommand.ExecuteAsync(null);

        Assert.NotNull(call);
        Assert.Equal("feature", call!.Value.branch);
        Assert.False(call.Value.create);
    }

    [Fact]
    public void DetachedWorktree_ShouldNotBlockAnyBranch()
    {
        // A detached worktree contributes no branch to the checked-out set.
        var vm = new WorktreePanelViewModel(FakeWith(("main", false, true), (null!, true, false)), "/repo")
        {
            NewWorktreePath = "../wt",
            SelectedBranch = "feature",
        };

        Assert.True(vm.CanCreate);
    }
}
