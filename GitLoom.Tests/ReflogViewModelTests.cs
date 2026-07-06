using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Services;
using GitLoom.App.ViewModels;
using GitLoom.Core;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-20 (VM) — the reflog viewer's two recovery actions in <see cref="ReflogViewModel"/>, driven
/// against a REAL journal-backed <see cref="GitService"/> on a fixture repo so the
/// "destructive action routes through the journal" requirement is proven end-to-end: a confirmed
/// Restore lands a journaled, undoable ResetToCommit; a declined Restore mutates nothing;
/// create-branch-here recovers an orphaned tip and is journaled; an empty name is rejected.
/// </summary>
public class ReflogViewModelTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly string _dbPath;
    private readonly OperationJournal _journal;
    private readonly GitService _git;

    public ReflogViewModelTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "gitloom-reflog-vm-" + Guid.NewGuid().ToString("N") + ".db");
        using (var ctx = new AppDbContext(_dbPath)) ctx.Database.Migrate();
        _journal = new OperationJournal(() => new AppDbContext(_dbPath));
        _git = new GitService(null, _journal);
    }

    public void Dispose()
    {
        _fx.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed class FakeConfirmationService : IConfirmationService
    {
        public bool Result { get; set; }
        public bool Asked { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText)
        {
            Asked = true;
            return Task.FromResult(Result);
        }
    }

    private string HeadSha() => _git.GetHeadState(_fx.RepoPath).Sha ?? "";

    private static Task Execute(IAsyncRelayCommand command) => command.ExecuteAsync(null);

    [Fact]
    public void Ctor_ShouldPopulateEntriesAndRefPicker()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var vm = new ReflogViewModel(_git, _fx.RepoPath);

        Assert.True(vm.HasEntries);
        Assert.Contains("HEAD", vm.RefNames);
        Assert.Contains(vm.Entries, e => e.ToSha == c1);
        // The creation entry renders a "—" from-sha and a "→" move label.
        Assert.Contains(vm.Entries, e => e.ShortFromSha == "—");
        Assert.All(vm.Entries, e => Assert.Contains("→", e.MoveText));
    }

    [Fact]
    public async Task Restore_WhenConfirmed_ShouldHardResetAndJournalUndoably()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var c2 = _fx.CommitFile("a.txt", "2\n", "second");
        var confirm = new FakeConfirmationService { Result = true };
        var vm = new ReflogViewModel(_git, _fx.RepoPath, confirm);

        var row = vm.Entries.First(e => e.ToSha == c1);
        await Execute(row.RestoreCommand);

        Assert.True(confirm.Asked);
        Assert.Equal(c1, HeadSha()); // hard-reset to the entry's target

        var entry = _journal.GetHistory(_fx.RepoPath).First(e => e.Kind == JournalKinds.ResetToCommit);
        Assert.True(entry.IsUndoable);

        // Journaled → undoable: undo puts HEAD back where it was.
        _journal.Undo(_fx.RepoPath, entry.Id);
        Assert.Equal(c2, HeadSha());
    }

    [Fact]
    public async Task Restore_WhenDeclined_ShouldChangeNothing()
    {
        _fx.CommitFile("a.txt", "1\n", "first");
        var c2 = _fx.CommitFile("a.txt", "2\n", "second");
        var confirm = new FakeConfirmationService { Result = false };
        var vm = new ReflogViewModel(_git, _fx.RepoPath, confirm);

        var row = vm.Entries.First(e => e.ToSha != c2);
        await Execute(row.RestoreCommand);

        Assert.True(confirm.Asked);
        Assert.Equal(c2, HeadSha()); // HEAD untouched
        Assert.DoesNotContain(_journal.GetHistory(_fx.RepoPath), e => e.Kind == JournalKinds.ResetToCommit);
    }

    [Fact]
    public async Task CreateBranchHere_ShouldRecoverOrphanedTip_AndJournal()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var c2 = _fx.CommitFile("a.txt", "2\n", "second");
        _git.ResetToCommit(_fx.RepoPath, c1, ResetMode.Hard); // c2 orphaned
        var vm = new ReflogViewModel(_git, _fx.RepoPath, new FakeConfirmationService());

        var row = vm.Entries.First(e => e.ToSha == c2);
        row.NewBranchName = "rescued";
        await Execute(row.CreateBranchHereCommand);

        Assert.Null(vm.ErrorMessage);
        using (var repo = new LibGit2Sharp.Repository(_fx.RepoPath))
            Assert.Equal(c2, repo.Branches["rescued"]?.Tip?.Sha);

        Assert.Contains(_journal.GetHistory(_fx.RepoPath), e => e.Kind == JournalKinds.CreateBranchAt);
    }

    [Fact]
    public async Task CreateBranchHere_WithEmptyName_ShouldErrorAndNotBranch()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var vm = new ReflogViewModel(_git, _fx.RepoPath, new FakeConfirmationService());

        var row = vm.Entries.First(e => e.ToSha == c1);
        row.NewBranchName = "   ";
        await Execute(row.CreateBranchHereCommand);

        Assert.NotNull(vm.ErrorMessage);
        Assert.DoesNotContain(_journal.GetHistory(_fx.RepoPath), e => e.Kind == JournalKinds.CreateBranchAt);
    }

    [Fact]
    public void SelectingBranchRef_ShouldReloadThatRefsReflog()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "first");
        var branch = _git.GetHeadState(_fx.RepoPath).CurrentBranchName!;
        var vm = new ReflogViewModel(_git, _fx.RepoPath, new FakeConfirmationService());

        vm.SelectedRef = branch; // triggers a reload against the branch reflog

        Assert.Null(vm.ErrorMessage);
        Assert.Contains(vm.Entries, e => e.ToSha == c1);
    }
}
