using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using GitLoom.App.Controls;
using GitLoom.App.Services;
using GitLoom.App.ViewModels;
using GitLoom.Core;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-09 #2–#4 (+ §3.3/§3.5) — commit context-menu construction, destructive-action routing,
/// the drag-drop merge/rebase flyout, and Delete-key branch deletion in
/// <see cref="CommitTimelineViewModel"/>. Menu construction lives in the ViewModel (testable),
/// so these assert the context rules, that hard reset / branch delete are gated by confirmation,
/// and the flyout wording + checkout-gating. Runs under the headless app ([AvaloniaFact]) because
/// the ViewModel wires the sidebar branch browser (reads <c>App.Settings</c>). Git effects are
/// verified behaviorally against a real fixture repo. Pins use an in-memory DB (never the real
/// app database).
/// </summary>
public class CommitTimelineMenuTests : IDisposable
{
    private readonly List<SqliteConnection> _connections = new();

    // Records whether confirmation was requested and returns a scripted answer.
    private sealed class FakeConfirmationService : IConfirmationService
    {
        public bool Result { get; set; }
        public bool Asked { get; private set; }
        public string? LastTitle { get; private set; }

        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText)
        {
            Asked = true;
            LastTitle = title;
            return Task.FromResult(Result);
        }
    }

    // A per-VM in-memory pinned-ref service so tests never touch the real app database.
    private IPinnedRefService InMemoryPins()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        _connections.Add(conn);
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        using (var ctx = new AppDbContext(options)) ctx.Database.EnsureCreated();
        return new PinnedRefService(() => new AppDbContext(options));
    }

    private CommitTimelineViewModel NewVm(GitService git, string repoPath, FakeConfirmationService? confirm = null)
        => new(git, repoPath, null, confirm, InMemoryPins());

    public void Dispose()
    {
        foreach (var c in _connections) c.Dispose();
        GC.SuppressFinalize(this);
    }

    private static IReadOnlyList<string> Headers(IEnumerable<MenuItemViewModel> items)
        => items.Where(i => i is not SeparatorViewModel).Select(i => i.Header).ToList();

    // --- Commit menu context rules ----------------------------------------------------------

    [AvaloniaFact]
    public void CommitMenu_ShouldIncludeContractItems_OnNonHeadCommit()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        fx.CommitFile("b.txt", "2\n", "c2"); // HEAD

        var headers = Headers(NewVm(git, fx.RepoPath).BuildCommitMenu(c1));

        Assert.Contains("Checkout (detached)", headers);
        Assert.Contains("Create branch here…", headers);
        Assert.Contains("Create tag here…", headers);
        Assert.Contains("Cherry-pick", headers);
        Assert.Contains("Revert", headers);
        Assert.Contains("Reset current branch here", headers);
        Assert.Contains("Interactive rebase onto here…", headers);
        Assert.Contains("Copy SHA", headers);
    }

    [AvaloniaFact]
    public void CommitMenu_ShouldHideCheckout_OnHeadCommit()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");
        var head = fx.CommitFile("b.txt", "2\n", "c2");

        var headers = Headers(NewVm(git, fx.RepoPath).BuildCommitMenu(head));

        Assert.DoesNotContain("Checkout (detached)", headers);
        Assert.Contains("Reset current branch here", headers);
        Assert.Contains("Copy SHA", headers);
    }

    [AvaloniaFact]
    public void CommitMenu_ShouldHideResetItems_WhenDetachedHead()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        var c2 = fx.CommitFile("b.txt", "2\n", "c2");

        git.CheckoutRevision(fx.RepoPath, c1); // detach
        Assert.True(git.GetHeadState(fx.RepoPath).IsDetached);

        var headers = Headers(NewVm(git, fx.RepoPath).BuildCommitMenu(c2));

        Assert.DoesNotContain("Reset current branch here", headers);
        Assert.Contains("Checkout (detached)", headers);
    }

    // --- Hard-reset confirmation gating -----------------------------------------------------

    [AvaloniaFact]
    public async Task HardReset_ShouldRequireConfirmation_AndNotMoveHead_WhenDeclined()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        var head = fx.CommitFile("b.txt", "2\n", "c2");

        var confirm = new FakeConfirmationService { Result = false };
        var vm = NewVm(git, fx.RepoPath, confirm);

        await vm.ResetCommitHardCommand.ExecuteAsync(c1);

        Assert.True(confirm.Asked);
        Assert.Equal(head, git.GetHeadState(fx.RepoPath).Sha); // unchanged — declined
    }

    [AvaloniaFact]
    public async Task HardReset_ShouldMoveHead_WhenConfirmed()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        fx.CommitFile("b.txt", "2\n", "c2");

        var confirm = new FakeConfirmationService { Result = true };
        var vm = NewVm(git, fx.RepoPath, confirm);

        await vm.ResetCommitHardCommand.ExecuteAsync(c1);

        Assert.True(confirm.Asked);
        Assert.Equal(c1, git.GetHeadState(fx.RepoPath).Sha);
    }

    [AvaloniaFact]
    public async Task SoftReset_ShouldMoveHead_WithoutConfirmation()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        fx.CommitFile("b.txt", "2\n", "c2");

        var confirm = new FakeConfirmationService { Result = false };
        var vm = NewVm(git, fx.RepoPath, confirm);

        await vm.ResetCommitSoftCommand.ExecuteAsync(c1);

        Assert.False(confirm.Asked);
        Assert.Equal(c1, git.GetHeadState(fx.RepoPath).Sha);
    }

    [AvaloniaFact]
    public async Task MixedReset_ShouldMoveHead_WithoutConfirmation()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        fx.CommitFile("b.txt", "2\n", "c2");

        var confirm = new FakeConfirmationService { Result = false };
        var vm = NewVm(git, fx.RepoPath, confirm);

        await vm.ResetCommitMixedCommand.ExecuteAsync(c1);

        Assert.False(confirm.Asked);
        Assert.Equal(c1, git.GetHeadState(fx.RepoPath).Sha);
    }

    // --- Hit → menu routing -----------------------------------------------------------------

    [AvaloniaFact]
    public void BuildContextMenuForHit_None_ShouldReturnNull()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");

        Assert.Null(NewVm(git, fx.RepoPath).BuildContextMenuForHit(new GraphHit(GraphHitKind.None, null, null)));
    }

    [AvaloniaFact]
    public void BuildContextMenuForHit_Node_ShouldReturnCommitMenu()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");

        var menu = NewVm(git, fx.RepoPath).BuildContextMenuForHit(new GraphHit(GraphHitKind.Node, c1, null));

        Assert.NotNull(menu);
        Assert.Contains("Copy SHA", Headers(menu!));
    }

    [AvaloniaFact]
    public void BuildContextMenuForHit_Label_ShouldReturnBranchMenu_AndArmSelection()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        fx.CreateBranch("feature");

        var vm = NewVm(git, fx.RepoPath);
        var menu = vm.BuildContextMenuForHit(new GraphHit(GraphHitKind.Label, c1, "feature"));

        Assert.NotNull(menu);
        Assert.Contains("Checkout", Headers(menu!)); // reuses the branch menu
        Assert.Contains("Pin", Headers(menu!));      // pin offered (not yet pinned)
        Assert.Equal("feature", vm.SelectedRefName); // Delete key is now armed on this ref
    }

    [AvaloniaFact]
    public void LabelMenu_ShouldOfferUnpin_AfterPinning()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var c1 = fx.CommitFile("a.txt", "1\n", "c1");
        fx.CreateBranch("feature");

        var vm = NewVm(git, fx.RepoPath);
        vm.PinRefCommand.Execute("feature");

        var headers = Headers(vm.BuildContextMenuForHit(new GraphHit(GraphHitKind.Label, c1, "feature"))!);
        Assert.Contains("Unpin", headers);
        Assert.DoesNotContain("Pin", headers.Where(h => h == "Pin"));
    }

    // --- Drag-drop merge/rebase flyout (§3.3) ----------------------------------------------

    [AvaloniaFact]
    public void DragMenu_TargetCheckedOut_ShouldOfferPlainMerge()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");

        var menu = NewVm(git, fx.RepoPath).BuildDragActionMenu("feature", "main", targetIsCheckedOut: true);

        Assert.Equal(2, menu.Count);
        Assert.Equal("Merge feature into main", menu[0].Header);
        Assert.Equal("Rebase feature onto main", menu[1].Header);
    }

    [AvaloniaFact]
    public void DragMenu_TargetNotCheckedOut_ShouldOfferCheckoutThenMerge()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");

        var menu = NewVm(git, fx.RepoPath).BuildDragActionMenu("feature", "main", targetIsCheckedOut: false);

        Assert.Equal("Checkout main, then merge feature", menu[0].Header);
        Assert.Equal("Rebase feature onto main", menu[1].Header);
    }

    [AvaloniaFact]
    public async Task MergeRefs_ShouldCheckOutTarget_BeforeMerging_WhenNotCurrent()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");
        var mainName = git.GetHeadState(fx.RepoPath).CurrentBranchName!;
        fx.CreateBranch("feature");             // at c1, not checked out
        fx.CommitFile("b.txt", "2\n", "c2");    // advance main

        var vm = NewVm(git, fx.RepoPath);
        // Drag main onto feature: the target ("feature") is not checked out, so it must be checked
        // out before the merge — never an in-memory merge against a non-checked-out branch.
        await vm.MergeRefsCommand.ExecuteAsync(new CommitTimelineViewModel.DragRefPair(mainName, "feature"));

        Assert.Equal("feature", git.GetHeadState(fx.RepoPath).CurrentBranchName);
    }

    // --- Delete key on a selected ref label (§3.5) -----------------------------------------

    [AvaloniaFact]
    public async Task DeleteSelectedRef_ShouldRequireConfirmation_AndKeepBranch_WhenDeclined()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");
        fx.CreateBranch("feature");

        var confirm = new FakeConfirmationService { Result = false };
        var vm = NewVm(git, fx.RepoPath, confirm);
        vm.SelectedRefName = "feature";

        await vm.DeleteSelectedRefCommand.ExecuteAsync(null);

        Assert.True(confirm.Asked);
        Assert.Contains(git.GetBranches(fx.RepoPath), b => b.FriendlyName == "feature");
    }

    [AvaloniaFact]
    public async Task DeleteSelectedRef_ShouldDeleteBranch_WhenConfirmed()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");
        fx.CreateBranch("feature");

        var confirm = new FakeConfirmationService { Result = true };
        var vm = NewVm(git, fx.RepoPath, confirm);
        vm.SelectedRefName = "feature";

        await vm.DeleteSelectedRefCommand.ExecuteAsync(null);

        Assert.True(confirm.Asked);
        Assert.DoesNotContain(git.GetBranches(fx.RepoPath), b => b.FriendlyName == "feature");
        Assert.Null(vm.SelectedRefName);
    }

    [AvaloniaFact]
    public async Task DeleteSelectedRef_ShouldNoOp_WhenNoRefSelected()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "c1");

        var confirm = new FakeConfirmationService { Result = true };
        var vm = NewVm(git, fx.RepoPath, confirm);
        vm.SelectedRefName = null;

        await vm.DeleteSelectedRefCommand.ExecuteAsync(null);

        Assert.False(confirm.Asked); // nothing selected → no confirmation, no delete
    }
}
