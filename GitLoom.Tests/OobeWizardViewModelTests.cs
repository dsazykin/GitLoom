using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-48 regression tests for the OOBE wizard view-model's interactive flow — specifically the
/// soft-lock class the owner hit: after an elevation failure ("Try again") or a consent cancel, the
/// re-shown Construct-Sandbox panel must ALWAYS have live buttons that drive the machine again. The
/// wizard runs the REAL <see cref="OobeStateMachine"/> over fakes for the side-effecting seams
/// (diagnostics probes, elevation launcher, bootstrap steps), so these are the exact code paths the
/// shipped wizard executes minus Windows itself.
/// </summary>
public class OobeWizardViewModelTests
{
    // ---- the owner's reported sequence: consent → elevation fails → Try again → consent again ----

    [Fact]
    public async Task Retry_AfterElevationFailure_ReshownConsentDrivesElevationAgain()
    {
        var launcher = new FakeElevationLauncher(
            _ => throw new BootstrapException("EnableFeatures", "elevation failed (attempt 1)"),
            _ => throw new BootstrapException("EnableFeatures", "elevation failed (attempt 2)"));
        var vm = CreateVm(launcher, out _);

        var run1 = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);

        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run1);
        Assert.Equal(OobePhase.Error, vm.Phase);
        Assert.Equal(1, launcher.Calls);
        Assert.Contains("attempt 1", vm.ErrorMessage);

        // "Try again" → the consent panel is re-shown by a FRESH machine pass…
        var run2 = vm.RetryCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);

        // …and its buttons must actually do something: a second elevation attempt.
        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run2);
        Assert.Equal(OobePhase.Error, vm.Phase);
        Assert.Equal(2, launcher.Calls);
        Assert.Contains("attempt 2", vm.ErrorMessage);
    }

    // ---- the provable dead-gate soft-lock: cancel at consent used to strand the user ----

    [Fact]
    public async Task CancelAtConsent_ThenConstruct_IsNeverSoftLocked()
    {
        var launcher = new FakeElevationLauncher(
            _ => new ElevatedHelperResult { FeaturesEnabled = true, RebootRequired = false, ResumeTaskRegistered = false });
        var vm = CreateVm(launcher, out _);

        var run1 = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);

        // Cancel at the gate: the pass ends, nothing was modified, the consent view is re-shown.
        vm.CancelConsentCommand.Execute(null);
        await WithTimeout(run1);
        Assert.Equal(OobePhase.Consent, vm.Phase);
        Assert.Equal(0, launcher.Calls);

        // The re-shown panel's Construct button must self-heal: start a fresh pass and carry the
        // consent through it (this used to be a permanent no-op on a completed TCS — the soft-lock).
        await WithTimeout(vm.ConstructSandboxCommand.ExecuteAsync(null));
        await WaitForPhaseAsync(vm, OobePhase.Done);
        Assert.Equal(1, launcher.Calls);
    }

    [Fact]
    public async Task CancelAtConsent_ReshownPanel_CancelStaysSafeNoOp()
    {
        var launcher = new FakeElevationLauncher();
        var vm = CreateVm(launcher, out _);

        var run1 = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.CancelConsentCommand.Execute(null);
        await WithTimeout(run1);

        // A second cancel with no live gate must not throw or start anything.
        vm.CancelConsentCommand.Execute(null);
        Assert.Equal(OobePhase.Consent, vm.Phase);
        Assert.Equal(0, launcher.Calls);
    }

    // ---- outcome mapping ----

    [Fact]
    public async Task Elevation_RebootRequired_ShowsRebootPhase()
    {
        var launcher = new FakeElevationLauncher(
            _ => new ElevatedHelperResult { FeaturesEnabled = true, RebootRequired = true, ResumeTaskRegistered = true });
        var vm = CreateVm(launcher, out _);

        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run);

        Assert.Equal(OobePhase.Reboot, vm.Phase);
    }

    [Fact]
    public async Task HelperReportedFailure_SurfacesTheHelpersError()
    {
        var launcher = new FakeElevationLauncher(
            _ => new ElevatedHelperResult
            {
                FeaturesEnabled = false,
                RebootRequired = false,
                ResumeTaskRegistered = false,
                Error = "powershell.exe exited 1: DISM exploded",
            });
        var vm = CreateVm(launcher, out _);

        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run);

        Assert.Equal(OobePhase.Error, vm.Phase);
        Assert.Contains("DISM exploded", vm.ErrorMessage);
    }

    [Fact]
    public async Task ResumeTaskRegistrationFailure_SurfacesTheHelpersError()
    {
        var launcher = new FakeElevationLauncher(
            _ => new ElevatedHelperResult
            {
                FeaturesEnabled = true,
                RebootRequired = true,
                ResumeTaskRegistered = false,
                Error = "schtasks.exe exited 1: Access is denied.",
            });
        var vm = CreateVm(launcher, out _);

        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run);

        Assert.Equal(OobePhase.Error, vm.Phase);
        Assert.Contains("Access is denied", vm.ErrorMessage);
        Assert.Contains("resumes setup after the restart", vm.ErrorMessage);
    }

    // ---- resumed run (persisted stage = EnableFeatures, e.g. a relaunched process) ----

    [Fact]
    public async Task ResumedRun_CancelAtConsent_StaysOnConsentNotAnEmptyDiagnosticsPanel()
    {
        var launcher = new FakeElevationLauncher();
        var vm = CreateVm(launcher, out var store);
        // Simulate the relaunched process: diagnostics already passed in an earlier run, so this pass
        // skips them and the Diagnostics collection stays empty.
        store.State = new OobeState { Stage = OobeStage.EnableFeatures };

        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.CancelConsentCommand.Execute(null);
        await WithTimeout(run);

        // Used to key off Diagnostics.Any() and dump the user on an idle empty diagnostics panel.
        Assert.Equal(OobePhase.Consent, vm.Phase);
    }

    // ---- P2-48 hardening: cross-process single-instance + resume-task hygiene ----

    [Fact]
    public async Task InstanceLockHeldElsewhere_ShowsActionableError_NotACorruptingSecondPass()
    {
        var launcher = new FakeElevationLauncher();
        var vm = CreateVm(launcher, out _, instanceLockFactory: static () => null); // "another process holds it"

        await WithTimeout(vm.StartCommand.ExecuteAsync(null));

        Assert.Equal(OobePhase.Error, vm.Phase);
        Assert.Contains("Another Mainguard setup is already running", vm.ErrorMessage);
        Assert.Equal(0, launcher.Calls); // the machine never ran — no state files were touched
    }

    [Fact]
    public async Task CompletedPass_SweepsTheResumeTask()
    {
        var launcher = new FakeElevationLauncher(
            _ => new ElevatedHelperResult { FeaturesEnabled = true, RebootRequired = false, ResumeTaskRegistered = false });
        var sweeps = 0;
        var swept = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = CreateVm(launcher, out _, resumeTaskSweep: () => { Interlocked.Increment(ref sweeps); swept.TrySetResult(); });

        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run);
        await WithTimeout(swept.Task); // the sweep runs off the pass's thread

        Assert.Equal(OobePhase.Done, vm.Phase);
        Assert.Equal(1, sweeps);
    }

    [Fact]
    public async Task AwaitingRebootPass_DoesNotSweep_TheTaskIsStillNeeded()
    {
        var launcher = new FakeElevationLauncher(
            _ => new ElevatedHelperResult { FeaturesEnabled = true, RebootRequired = true, ResumeTaskRegistered = true });
        var sweeps = 0;
        var vm = CreateVm(launcher, out _, resumeTaskSweep: () => Interlocked.Increment(ref sweeps));

        var run = vm.StartCommand.ExecuteAsync(null);
        await WaitForPhaseAsync(vm, OobePhase.Consent);
        vm.ConstructSandboxCommand.Execute(null);
        await WithTimeout(run);

        Assert.Equal(OobePhase.Reboot, vm.Phase);
        Assert.Equal(0, sweeps); // deleting here would strand the post-reboot resume
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static OobeWizardViewModel CreateVm(
        FakeElevationLauncher launcher,
        out FakeStore store,
        Action? resumeTaskSweep = null,
        Func<OobeInstanceLock?>? instanceLockFactory = null)
    {
        store = new FakeStore();
        var machine = new OobeStateMachine(store);
        var diagnostics = new SystemDiagnostics(new PassingSystemProbe(), new ReadyWslProbe());
        var bootstrapper = new GitLoomOsBootstrapper(new IBootstrapStep[] { new SatisfiedStep() });
        return new OobeWizardViewModel(machine, diagnostics, launcher, bootstrapper, resumeTaskSweep, instanceLockFactory);
    }

    // Event-driven (PropertyChanged→TCS), not a 10ms poll: under xUnit's cross-class parallelism the
    // heavy suites (terminal firehose, dock teardown, render harness) can starve the thread pool for
    // seconds, and a short polling deadline turned real passes into flaky failures. The timeout is
    // deliberately deadlock-scale — it should only ever trip on a genuinely hung wizard pass.
    private const int DeadlockTimeoutMs = 60_000;

    private static async Task WaitForPhaseAsync(OobeWizardViewModel vm, OobePhase phase, int timeoutMs = DeadlockTimeoutMs)
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
            var finished = await Task.WhenAny(reached.Task, Task.Delay(timeoutMs));
            Assert.True(ReferenceEquals(finished, reached.Task),
                $"timed out waiting for wizard phase {phase} (current: {vm.Phase})");
        }
        finally
        {
            vm.PropertyChanged -= OnChanged;
        }
    }

    private static async Task WithTimeout(Task task, int timeoutMs = DeadlockTimeoutMs)
    {
        var finished = await Task.WhenAny(task, Task.Delay(timeoutMs));
        Assert.True(ReferenceEquals(finished, task), "timed out waiting for the wizard pass to finish");
        await task; // propagate faults
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
        private readonly Queue<Func<CancellationToken, ElevatedHelperResult>> _script;
        public int Calls { get; private set; }

        public FakeElevationLauncher(params Func<CancellationToken, ElevatedHelperResult>[] script) =>
            _script = new Queue<Func<CancellationToken, ElevatedHelperResult>>(script);

        public Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct)
        {
            Calls++;
            if (_script.Count == 0)
                throw new InvalidOperationException("unexpected elevation attempt (script exhausted)");
            return Task.FromResult(_script.Dequeue()(ct));
        }
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
