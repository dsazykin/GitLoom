using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// PR2 — the OOBE repo-onboarding step (<see cref="OobePhase.RepoOnboarding"/>): the two entry
/// choices (one folder scanned with the existing discovery walk + <c>AutoDetectPath</c> persisted;
/// individual picks validated per folder), the default-checked results list, the sequential
/// copy-into-Mainguard-OS run with per-row failure isolation and cancellation, the state-derived
/// footer matrix, and the guarantee that the step can never fail the OOBE (skip always finishes).
/// Mirrors <see cref="OobeWizardViewModelTests"/>: the REAL <see cref="OobeStateMachine"/> over
/// fakes for every side-effecting seam.
/// </summary>
public class OobeRepoOnboardingTests
{
    // ---- step placement: after the machine completes (and after the CLI step), before Done ----

    [Fact]
    public async Task CompletedPass_WithRepoSeams_LandsOnRepoOnboarding_NotDone()
    {
        var vm = await CreateVmOnRepoStepAsync(new Seams());

        Assert.Equal(OobePhase.RepoOnboarding, vm.Phase);
        Assert.True(vm.IsRepoChoice); // nothing scanned yet — the two-choice view
        Assert.True(vm.ShowSkipRepos); // and the way out is live immediately
    }

    [Fact]
    public async Task CompletedPass_WithoutRepoSeams_StillLandsOnDone()
    {
        var vm = await RunMachineToCompletionAsync(seams: null);

        Assert.Equal(OobePhase.Done, vm.Phase);
    }

    [Fact]
    public async Task SkipWithZeroReposOnboarded_StillFinishesSetup()
    {
        var vm = await CreateVmOnRepoStepAsync(new Seams());

        vm.FinishRepoStepCommand.Execute(null);

        Assert.Equal(OobePhase.Done, vm.Phase);
    }

    // ---- choice A: one folder of repos ----

    [Fact]
    public async Task PickRepoFolder_ScansWithDiscovery_PersistsAutoDetectPath_RowsDefaultChecked()
    {
        var seams = new Seams
        {
            PickRoot = () => Task.FromResult<string?>(@"C:\code"),
            Discover = root => new[] { @"C:\code\alpha", @"C:\code\beta" },
        };
        var vm = await CreateVmOnRepoStepAsync(seams);

        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\code", seams.Settings.Current.AutoDetectPath); // the EXISTING preference
        Assert.Equal(new[] { @"C:\code\alpha", @"C:\code\beta" }, vm.RepoRows.Select(r => r.Path));
        Assert.All(vm.RepoRows, r => Assert.True(r.IsSelected)); // default checked
        Assert.False(vm.IsRepoChoice); // results list replaces the choice view
        Assert.True(vm.ShowCopyReposAccent); // nothing onboarded yet → Copy is the one Accent
        Assert.True(vm.ShowSkipRepos);
    }

    [Fact]
    public async Task PickRepoFolder_Dismissed_NothingChanges()
    {
        var seams = new Seams { PickRoot = () => Task.FromResult<string?>(null) };
        var vm = await CreateVmOnRepoStepAsync(seams);

        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        Assert.True(vm.IsRepoChoice);
        Assert.Empty(vm.RepoRows);
        Assert.Equal(string.Empty, seams.Settings.Current.AutoDetectPath); // nothing persisted
    }

    [Fact]
    public async Task PickRepoFolder_EmptyScan_ShowsNoticeAndStaysOnChoice()
    {
        var seams = new Seams
        {
            PickRoot = () => Task.FromResult<string?>(@"C:\empty"),
            Discover = _ => Array.Empty<string>(),
        };
        var vm = await CreateVmOnRepoStepAsync(seams);

        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        Assert.True(vm.IsRepoChoice); // both choices stay available
        Assert.True(vm.HasRepoNotice);
        Assert.Contains(@"C:\empty", vm.RepoNotice);
    }

    // ---- choice B: individual picks, each validated ----

    [Fact]
    public async Task PickIndividualRepos_ValidatesEach_SkipsNonRepos_DedupesByPath()
    {
        var seams = new Seams
        {
            PickMany = () => Task.FromResult<IReadOnlyList<string>>(
                new[] { @"C:\code\alpha", @"C:\docs\not-a-repo", @"C:\code\alpha" }),
            IsRepo = path => !path.Contains("not-a-repo"),
        };
        var vm = await CreateVmOnRepoStepAsync(seams);

        await vm.PickIndividualReposCommand.ExecuteAsync(null);

        var row = Assert.Single(vm.RepoRows); // valid once, duplicate dropped
        Assert.Equal(@"C:\code\alpha", row.Path);
        Assert.Equal("alpha", row.Name);
        Assert.True(row.IsSelected);
        Assert.True(vm.HasRepoNotice); // the invalid pick is named, not silently dropped
        Assert.Contains("not-a-repo", vm.RepoNotice);
    }

    // ---- the copy run: sequential, persisted, footer flips to Continue ----

    [Fact]
    public async Task CopySelected_ProvisionsSequentially_PersistsEachRepo_FooterBecomesContinue()
    {
        var provisioned = new List<string>();
        var persisted = new List<string>();
        var seams = new Seams
        {
            Provision = (path, _) => { provisioned.Add(path); return Task.CompletedTask; },
            Persist = persisted.Add,
        };
        var vm = await CreateVmOnRepoStepAsync(seams);
        await ScanTwoReposAsync(vm);

        await vm.CopySelectedReposCommand.ExecuteAsync(null);

        Assert.Equal(new[] { @"C:\code\alpha", @"C:\code\beta" }, provisioned);
        Assert.Equal(provisioned, persisted); // into the ONE sidebar store, once per success
        Assert.All(vm.RepoRows, r => Assert.True(r.IsOnboarded));
        Assert.True(vm.ShowContinueRepos); // something onboarded → Continue is the Accent
        Assert.False(vm.ShowSkipRepos);
        Assert.False(vm.ShowCopyReposAccent); // nothing left to copy
        Assert.False(vm.ShowCopyReposPrimary);
        Assert.False(vm.IsProvisioningRepos);
    }

    [Fact]
    public async Task CopySelected_UncheckedRowIsLeftAlone()
    {
        var provisioned = new List<string>();
        var seams = new Seams { Provision = (path, _) => { provisioned.Add(path); return Task.CompletedTask; } };
        var vm = await CreateVmOnRepoStepAsync(seams);
        await ScanTwoReposAsync(vm);
        vm.RepoRows[1].IsSelected = false;

        await vm.CopySelectedReposCommand.ExecuteAsync(null);

        Assert.Equal(new[] { @"C:\code\alpha" }, provisioned);
        Assert.False(vm.RepoRows[1].IsOnboarded);
        Assert.True(vm.ShowCopyReposPrimary); // one onboarded + one still onboardable → demoted Copy
        Assert.True(vm.ShowContinueRepos);
    }

    // ---- failure isolation: one bad repo can never take down the run or the OOBE ----

    [Fact]
    public async Task CopySelected_FailingRepo_ShowsCauseOnItsRow_RestStillCopy_SetupStillFinishes()
    {
        var persisted = new List<string>();
        var seams = new Seams
        {
            Discover = _ => new[] { @"C:\code\alpha", @"C:\code\bad", @"C:\code\gamma" },
            Provision = (path, _) => path.Contains("bad")
                ? Task.FromException(new InvalidOperationException("mirror clone refused"))
                : Task.CompletedTask,
            Persist = persisted.Add,
        };
        var vm = await CreateVmOnRepoStepAsync(seams);
        await vm.PickRepoFolderCommand.ExecuteAsync(null);

        await vm.CopySelectedReposCommand.ExecuteAsync(null);

        Assert.Equal(new[] { @"C:\code\alpha", @"C:\code\gamma" }, persisted); // a failed repo is never persisted
        var bad = vm.RepoRows.Single(r => r.Path.Contains("bad"));
        Assert.True(bad.IsFailed);
        Assert.Contains("mirror clone refused", bad.StatusMessage); // the actionable cause, on its row
        Assert.False(bad.IsOnboarded);
        Assert.True(vm.RepoRows.Single(r => r.Path.Contains("gamma")).IsOnboarded); // the rest continued
        Assert.True(vm.ShowContinueRepos); // the step is still finishable

        vm.FinishRepoStepCommand.Execute(null);
        Assert.Equal(OobePhase.Done, vm.Phase); // repo trouble never fails the OOBE
    }

    // ---- cancellation mid-run: in-flight row reports it, later rows keep their checkbox ----

    [Fact]
    public async Task CancelMidRun_InFlightRowReportsCancelled_LaterRowsUntouched()
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
        var vm = await CreateVmOnRepoStepAsync(seams);
        await ScanTwoReposAsync(vm);

        var run = vm.CopySelectedReposCommand.ExecuteAsync(null);
        await WithTimeout(firstStarted.Task);
        vm.CancelRepoCopyCommand.Execute(null);
        await WithTimeout(run);

        Assert.False(vm.IsProvisioningRepos);
        var first = vm.RepoRows[0];
        Assert.False(first.IsOnboarded);
        Assert.Contains("Cancelled", first.StatusMessage);
        var second = vm.RepoRows[1];
        Assert.True(second.IsSelected); // still checked — re-Copy or Skip both work
        Assert.Null(second.StatusMessage);
        Assert.True(vm.ShowSkipRepos); // nothing onboarded → the skip path is live
    }

    // ---- choose again: back to the choice view before anything was copied ----

    [Fact]
    public async Task ChooseAgain_ClearsRowsBackToChoice()
    {
        var seams = new Seams();
        var vm = await CreateVmOnRepoStepAsync(seams);
        await ScanTwoReposAsync(vm);
        Assert.True(vm.ShowRepoChooseAgain);

        vm.ChooseReposAgainCommand.Execute(null);

        Assert.Empty(vm.RepoRows);
        Assert.True(vm.IsRepoChoice);
        Assert.False(vm.HasRepoNotice);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The step's injectable seams, all defaulting to benign fakes.</summary>
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

    private static async Task ScanTwoReposAsync(OobeWizardViewModel vm)
    {
        await vm.PickRepoFolderCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.RepoRows.Count);
    }

    private static async Task<OobeWizardViewModel> CreateVmOnRepoStepAsync(Seams seams)
    {
        var vm = await RunMachineToCompletionAsync(seams);
        Assert.Equal(OobePhase.RepoOnboarding, vm.Phase);
        return vm;
    }

    /// <summary>Runs the real state machine to Completed (diagnostics pass → consent → elevation
    /// succeeds, no reboot → import satisfied), landing the wizard on the step under test.</summary>
    private static async Task<OobeWizardViewModel> RunMachineToCompletionAsync(Seams? seams)
    {
        var machine = new OobeStateMachine(new FakeStore());
        var diagnostics = new SystemDiagnostics(new PassingSystemProbe(), new ReadyWslProbe());
        var launcher = new FakeElevationLauncher();
        var bootstrapper = new MainguardOsBootstrapper(new IBootstrapStep[] { new SatisfiedStep() });
        var discovery = seams is null ? null : new FakeDiscovery(seams);
        var vm = new OobeWizardViewModel(
            machine, diagnostics, launcher, bootstrapper,
            repoDiscovery: discovery,
            pickRepoRootFolder: seams is null ? null : () => seams.PickRoot(),
            pickIndividualRepoFolders: seams is null ? null : () => seams.PickMany(),
            provisionRepo: seams is null ? null : (path, ct) => seams.Provision(path, ct),
            persistRepo: seams is null ? null : path => seams.Persist(path),
            settingsService: seams?.Settings);

        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run);
        return vm;
    }

    // Event-driven wait + deadlock-scale timeout, exactly like OobeWizardViewModelTests (the pool
    // can be starved for seconds by the heavy suites under xUnit's cross-class parallelism).
    private const int DeadlockTimeoutMs = 60_000;

    private static async Task WaitForPhaseAsync(OobeWizardViewModel vm, OobePhase phase)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? _, System.ComponentModel.PropertyChangedEventArgs __)
        {
            if (vm.Phase == phase)
                reached.TrySetResult();
        }

        vm.PropertyChanged += OnChanged;
        try
        {
            if (vm.Phase == phase)
                return;
            var finished = await Task.WhenAny(reached.Task, Task.Delay(DeadlockTimeoutMs));
            Assert.True(ReferenceEquals(finished, reached.Task),
                $"timed out waiting for wizard phase {phase} (current: {vm.Phase})");
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }
    }

    private static async Task WithTimeout(Task task)
    {
        var finished = await Task.WhenAny(task, Task.Delay(DeadlockTimeoutMs));
        Assert.True(ReferenceEquals(finished, task), "timed out waiting for the wizard pass to finish");
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

    private sealed class FakeStore : IOobeStateStore
    {
        public OobeState? State { get; set; }
        public OobeState? Load() => State;
        public void Save(OobeState state) => State = state;
        public void Clear() => State = null;
    }

    private sealed class FakeElevationLauncher : IElevationLauncher
    {
        public Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct) =>
            Task.FromResult(new ElevatedHelperResult
            {
                FeaturesEnabled = true,
                RebootRequired = false,
                ResumeTaskRegistered = false,
            });
    }

    private sealed class PassingSystemProbe : ISystemProbe
    {
        public Architecture OsArchitecture => Architecture.X64;
        public OsBuildInfo GetOsBuild() => new(true, 10, 26100);
        public VirtualizationInfo GetVirtualization() => new(true, true);
        public long GetFreeDiskBytes() => 200L * 1024 * 1024 * 1024;
        public bool IsUserAdministrator() => true;
    }

    private sealed class ReadyWslProbe : IWslStatusProbe
    {
        public Task<WslStatusReport> QueryAsync(CancellationToken ct) =>
            Task.FromResult(new WslStatusReport(WslInstallState.Wsl2Ready, "2", "2.1.5", "5.15"));
    }

    private sealed class SatisfiedStep : IBootstrapStep
    {
        public string Name => "Fake import step";
        public Task<bool> IsSatisfiedAsync(CancellationToken ct) => Task.FromResult(true);
        public Task ExecuteAsync(IProgress<string> log, CancellationToken ct) => Task.CompletedTask;
    }
}
