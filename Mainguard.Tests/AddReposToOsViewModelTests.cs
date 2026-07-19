using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The post-setup "Add Repos to GitLoom OS" window (Tools menu) — <see cref="AddReposToOsViewModel"/>
/// over the shared <see cref="RepoOnboardingViewModel"/> engine, driven over fake seams exactly like
/// <see cref="OobeRepoOnboardingTests"/> drives the OOBE step (and <c>AgentCliUiTests</c> the CLI
/// surfaces): honest empty state on a fruitless scan, per-repo failure isolation with a retryable
/// row, an actionable daemon-unreachable message (never a crash), quiet success for a repo that is
/// already in GitLoom OS (the pipeline is idempotent end to end), mid-run cancellation, and the
/// window Close wiring. Because the window IS the OOBE step's engine, these tests also pin the
/// two surfaces to one behaviour.
/// </summary>
public class AddReposToOsViewModelTests
{
    // ---- entry state: the two-choice view, Close always live ----

    [Fact]
    public void OpensOnChoiceView_NothingScannedYet()
    {
        var vm = Create(new Seams());

        Assert.True(vm.IsRepoChoice);
        Assert.Empty(vm.RepoRows);
        Assert.False(vm.IsProvisioningRepos);
        Assert.True(vm.CanOnboard);
    }

    [Fact]
    public void Close_InvokesTheWindowCloseAction()
    {
        var closed = false;
        var vm = Create(new Seams());
        vm.CloseAction = () => closed = true;

        vm.CloseCommand.Execute(null);

        Assert.True(closed);
    }

    // ---- discovery: honest empty state, persisted auto-detect path, validated picks ----

    [Fact]
    public async Task PickFolder_EmptyScan_ShowsHonestNotice_StaysOnChoice()
    {
        var seams = new Seams
        {
            PickRoot = () => Task.FromResult<string?>(@"C:\empty"),
            Discover = _ => Array.Empty<string>(),
        };
        var vm = Create(seams);

        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        Assert.True(vm.IsRepoChoice); // both choices stay available — never a dead blank panel
        Assert.True(vm.HasRepoNotice);
        Assert.Contains(@"C:\empty", vm.RepoNotice);
    }

    [Fact]
    public async Task PickFolder_RowsDefaultChecked_AutoDetectPathPersisted()
    {
        var seams = new Seams
        {
            PickRoot = () => Task.FromResult<string?>(@"C:\code"),
            Discover = _ => new[] { @"C:\code\alpha", @"C:\code\beta" },
        };
        var vm = Create(seams);

        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\code", seams.Settings.Current.AutoDetectPath); // the EXISTING preference
        Assert.Equal(new[] { @"C:\code\alpha", @"C:\code\beta" }, vm.RepoRows.Select(r => r.Path));
        Assert.All(vm.RepoRows, r => Assert.True(r.IsSelected));
        Assert.True(vm.ShowCopyReposAccent); // nothing onboarded yet → Copy is the one Accent
    }

    [Fact]
    public async Task PickIndividual_ValidatesEach_SkipsNonRepos_Dedupes()
    {
        var seams = new Seams
        {
            PickMany = () => Task.FromResult<IReadOnlyList<string>>(
                new[] { @"C:\code\alpha", @"C:\docs\not-a-repo", @"C:\code\alpha" }),
            IsRepo = path => !path.Contains("not-a-repo"),
        };
        var vm = Create(seams);

        await vm.PickIndividualReposCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.RepoRows);
        Assert.Equal(@"C:\code\alpha", row.Path);
        Assert.True(vm.HasRepoNotice); // the invalid pick is named, not silently dropped
        Assert.Contains("not-a-repo", vm.RepoNotice);
    }

    // ---- the copy run: sequential + persisted, and idempotent for already-provisioned repos ----

    [Fact]
    public async Task CopySelected_ProvisionsSequentially_PersistsEach_FooterFlips()
    {
        var provisioned = new List<string>();
        var persisted = new List<string>();
        var seams = new Seams
        {
            Provision = (path, _) => { provisioned.Add(path); return Task.CompletedTask; },
            Persist = persisted.Add,
        };
        var vm = await CreateWithTwoReposAsync(seams);

        await vm.CopySelectedReposCommand.ExecuteAsync(null);

        Assert.Equal(new[] { @"C:\code\alpha", @"C:\code\beta" }, provisioned);
        Assert.Equal(provisioned, persisted); // into the ONE sidebar store, once per success
        Assert.All(vm.RepoRows, r => Assert.True(r.IsOnboarded));
        Assert.True(vm.ShowContinueRepos);
        Assert.False(vm.ShowCopyReposAccent); // nothing left to copy
    }

    [Fact]
    public async Task AlreadyProvisionedRepo_SucceedsQuietly_NothingDuplicated()
    {
        // The daemon's ProvisionRepo and the sync-remote registration are both idempotent — a repo
        // that is already in GitLoom OS just completes again. The row must land on the plain
        // "In GitLoom OS" success (no error, no special banner), and persistence stays idempotent
        // too (RepoCatalog.EnsureRegistered dedupes by path — here we just assert one call per run).
        var persistCalls = 0;
        var seams = new Seams
        {
            Discover = _ => new[] { @"C:\code\already-in-os" },
            Provision = (_, _) => Task.CompletedTask, // the daemon reports success for a re-provision
            Persist = _ => persistCalls++,
        };
        var vm = Create(seams);
        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        await vm.CopySelectedReposCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.RepoRows);
        Assert.True(row.IsOnboarded);
        Assert.False(row.IsFailed);
        Assert.Null(row.StatusMessage); // quiet success — the "In GitLoom OS" chip says the rest
        Assert.Equal(1, persistCalls);
    }

    // ---- failure isolation + retry: one bad repo never takes down the run or the window ----

    [Fact]
    public async Task FailingRepo_IsIsolated_OthersStillCopy_AndTheRowIsRetryable()
    {
        var attempts = new List<string>();
        var failFirstAttempt = true;
        var seams = new Seams
        {
            Discover = _ => new[] { @"C:\code\alpha", @"C:\code\bad", @"C:\code\gamma" },
            Provision = (path, _) =>
            {
                attempts.Add(path);
                if (path.Contains("bad") && failFirstAttempt)
                    return Task.FromException(new InvalidOperationException("mirror clone refused"));
                return Task.CompletedTask;
            },
        };
        var vm = Create(seams);
        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        await vm.CopySelectedReposCommand.ExecuteAsync(null);

        var bad = vm.RepoRows.Single(r => r.Path.Contains("bad"));
        Assert.True(bad.IsFailed);
        Assert.Contains("mirror clone refused", bad.StatusMessage); // the actionable cause, on its row
        Assert.True(vm.RepoRows.Single(r => r.Path.Contains("gamma")).IsOnboarded); // the rest continued

        // Retry: the failed row kept its checkbox — Copy again re-runs exactly it.
        Assert.True(bad.IsSelected);
        Assert.True(vm.CopySelectedReposCommand.CanExecute(null));
        failFirstAttempt = false;
        await vm.CopySelectedReposCommand.ExecuteAsync(null);

        Assert.True(bad.IsOnboarded);
        Assert.False(bad.IsFailed);
        Assert.Equal(4, attempts.Count); // 3 first pass + only the failed one on retry
    }

    [Fact]
    public async Task DaemonUnreachable_ShowsActionableCause_NeverACrash_RetryStaysLive()
    {
        var seams = new Seams
        {
            Provision = (_, _) => Task.FromException(new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "connection refused"))),
        };
        var vm = await CreateWithTwoReposAsync(seams);

        await vm.CopySelectedReposCommand.ExecuteAsync(null); // must not throw

        Assert.All(vm.RepoRows, r => Assert.True(r.IsFailed));
        var message = vm.RepoRows[0].StatusMessage;
        Assert.NotNull(message);
        Assert.Contains("Mainguard OS could not be reached", message); // names the cause, not a gRPC code
        Assert.Contains("automatically the first time you open it", message); // and the way out
        Assert.True(vm.ShowCopyReposAccent); // nothing onboarded, rows still checked → retry is live
        Assert.True(vm.CopySelectedReposCommand.CanExecute(null));
    }

    // ---- cancellation mid-run: in-flight row reports it, later rows keep their checkbox ----

    [Fact]
    public async Task CancelMidRun_InFlightRowReportsCancelled_LaterRowUntouched()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seams = new Seams
        {
            Provision = async (path, ct) =>
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // parked until cancelled
            },
        };
        var vm = await CreateWithTwoReposAsync(seams);

        var run = vm.CopySelectedReposCommand.ExecuteAsync(null);
        await WithTimeout(firstStarted.Task);
        vm.CancelRepoCopyCommand.Execute(null);
        await WithTimeout(run);

        Assert.False(vm.IsProvisioningRepos);
        Assert.False(vm.RepoRows[0].IsOnboarded);
        Assert.Contains("Cancelled", vm.RepoRows[0].StatusMessage);
        Assert.True(vm.RepoRows[1].IsSelected); // still checked — Copy again or Close both work
        Assert.Null(vm.RepoRows[1].StatusMessage);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The window's injectable seams, all defaulting to benign fakes (mirrors
    /// <see cref="OobeRepoOnboardingTests"/> so the two surfaces are tested in one vocabulary).</summary>
    private sealed class Seams
    {
        public Func<Task<string?>> PickRoot = () => Task.FromResult<string?>(@"C:\code");
        public Func<Task<IReadOnlyList<string>>> PickMany = () => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Func<string, IReadOnlyList<string>> Discover = _ => new[] { @"C:\code\alpha", @"C:\code\beta" };
        public Func<string, bool> IsRepo = _ => true;
        public Func<string, CancellationToken, Task> Provision = (_, _) => Task.CompletedTask;
        public Action<string> Persist = _ => { };
        public FakeSettings Settings { get; } = new();
    }

    private static AddReposToOsViewModel Create(Seams seams) => new(
        new FakeDiscovery(seams),
        () => seams.PickRoot(),
        () => seams.PickMany(),
        (path, ct) => seams.Provision(path, ct),
        path => seams.Persist(path),
        seams.Settings);

    private static async Task<AddReposToOsViewModel> CreateWithTwoReposAsync(Seams seams)
    {
        var vm = Create(seams);
        await vm.PickRepoFolderCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.RepoRows.Count);
        return vm;
    }

    // Deadlock-scale timeout, exactly like OobeRepoOnboardingTests (the pool can be starved for
    // seconds by the heavy suites under xUnit's cross-class parallelism).
    private const int DeadlockTimeoutMs = 60_000;

    private static async Task WithTimeout(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(DeadlockTimeoutMs));
        Assert.True(ReferenceEquals(finished, task), "timed out waiting for the copy run to finish");
        await task; // propagate faults
    }

    private sealed class FakeDiscovery : IRepoDiscoveryService
    {
        private readonly Seams _seams;
        public FakeDiscovery(Seams seams) => _seams = seams;
        public IReadOnlyList<string> DiscoverRepositories(string rootPath) => _seams.Discover(rootPath);
        public bool IsGitRepository(string path) => _seams.IsRepo(path);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public UserPreferences Current { get; } = new();
        public void Update(Action<UserPreferences> updateAction) => updateAction(Current);
        public void Load() { }
        public void Save() { }
    }
}
