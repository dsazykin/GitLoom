using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The tier-2 upgrade offer surface: consent-first (nothing runs until "Upgrade now"), decline
/// notifies the App's session flag and closes without touching the orchestrator, the run drives
/// the plan-step checklist, and failures — including the stranded-after-retire state — surface the
/// orchestrator's typed message honestly (the stranded VHDX path shown, never a fake success).
/// The <c>LogSink</c> seam (the App's oobe.log writer) receives every progress line plus one
/// final-result line (outcome, promote strategy, stranded path) — the dialog is never the only
/// witness to a failed upgrade.
/// </summary>
public class VmUpgradeOfferViewModelTests
{
    private static readonly VmUpgradeOptions Options = new(
        @"C:\app\payload\GitLoomOS.tar.gz", @"C:\data\vm-staging", @"C:\data\vm");

    [Fact]
    public void StartsInTheOfferState_WithThePlanStepsSeededPending()
    {
        var vm = new VmUpgradeOfferViewModel(new FakeOrchestrator(), Options, "0.1.0", "0.2.0");

        Assert.True(vm.IsOffering);
        Assert.False(vm.IsRunning);
        Assert.Equal(VmUpgradePlan.Steps().Select(s => s.Description), vm.Steps.Select(s => s.Name));
        Assert.All(vm.Steps, s => Assert.True(s.IsPending));
        Assert.Equal("0.1.0", vm.InstalledVersion);
        Assert.Equal("0.2.0", vm.ExpectedVersion);
    }

    [Fact]
    public void Later_InvokesDeclined_ClosesTheWindow_AndNeverRunsTheOrchestrator()
    {
        var orchestrator = new FakeOrchestrator();
        var declined = false;
        var closed = false;
        var vm = new VmUpgradeOfferViewModel(orchestrator, Options, "0.1.0", "0.2.0")
        {
            Declined = () => declined = true,
            CloseAction = () => closed = true,
        };

        vm.LaterCommand.Execute(null);

        Assert.True(declined);
        Assert.True(closed);
        Assert.Null(orchestrator.ReceivedOptions);
    }

    [Fact]
    public async Task Upgrade_RunsTheOrchestratorWithTheOptions_AndCompletesWithAllStepsDone()
    {
        var orchestrator = new FakeOrchestrator();
        var vm = new VmUpgradeOfferViewModel(orchestrator, Options, "0.1.0", "0.2.0");

        await RunOnImmediateContextAsync(() => vm.UpgradeCommand.ExecuteAsync(null));

        Assert.Same(Options, orchestrator.ReceivedOptions);
        Assert.False(vm.IsOffering);
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsComplete);
        Assert.False(vm.HasError);
        Assert.All(vm.Steps, s => Assert.True(s.IsDone));
    }

    [Fact]
    public async Task Upgrade_ProgressLines_AdvanceTheStepChecklist()
    {
        var steps = VmUpgradePlan.Steps();
        var orchestrator = new FakeOrchestrator
        {
            // Reports the first two step descriptions plus a detail line, then fails on step two.
            OnRun = progress =>
            {
                progress?.Report(steps[0].Description);
                progress?.Report(steps[1].Description);
                progress?.Report("Migrating /home/gitloom/gitloom into the new environment…");
            },
            Result = new VmUpgradeResult(false, VmUpgradeFailureKind.OldDistroIntact, "tar failed"),
        };
        var vm = new VmUpgradeOfferViewModel(orchestrator, Options, "0.1.0", "0.2.0");

        await RunOnImmediateContextAsync(() => vm.UpgradeCommand.ExecuteAsync(null));

        Assert.True(vm.Steps[0].IsDone);
        Assert.True(vm.Steps[1].IsFailed); // the running step at failure time
        Assert.Equal("Migrating /home/gitloom/gitloom into the new environment…", vm.Steps[1].LogTail);
        Assert.True(vm.Steps[2].IsPending);
        Assert.True(vm.HasError);
        Assert.Equal("tar failed", vm.ErrorMessage);
        Assert.False(vm.IsStranded);
    }

    [Fact]
    public async Task Upgrade_StrandedFailure_SurfacesTheVhdxPath()
    {
        var orchestrator = new FakeOrchestrator
        {
            Result = new VmUpgradeResult(
                false, VmUpgradeFailureKind.StrandedAfterRetire,
                @"stranded — data at 'C:\data\vm\ext4.vhdx'", @"C:\data\vm\ext4.vhdx"),
        };
        var vm = new VmUpgradeOfferViewModel(orchestrator, Options, "0.1.0", "0.2.0");

        await RunOnImmediateContextAsync(() => vm.UpgradeCommand.ExecuteAsync(null));

        Assert.True(vm.HasError);
        Assert.True(vm.IsStranded);
        Assert.Equal(@"C:\data\vm\ext4.vhdx", vm.StrandedVhdxPath);
        Assert.False(vm.IsComplete);
    }

    [Fact]
    public async Task Upgrade_LogSink_ReceivesEveryProgressLine_AndTheFinalTypedResult()
    {
        var steps = VmUpgradePlan.Steps();
        var orchestrator = new FakeOrchestrator
        {
            OnRun = progress =>
            {
                progress?.Report(steps[0].Description);
                progress?.Report("Migrating /home/gitloom/gitloom into the new environment…");
            },
            Result = new VmUpgradeResult(true, VmUpgradeFailureKind.None, "upgraded", PromoteStrategy: "move"),
        };
        var log = new List<string>();
        var vm = new VmUpgradeOfferViewModel(orchestrator, Options, "0.1.0", "0.2.0") { LogSink = log.Add };

        await RunOnImmediateContextAsync(() => vm.UpgradeCommand.ExecuteAsync(null));

        // The upgrade tells its whole story in the log: accept, every progress line, final result.
        Assert.Contains(log, l => l.Contains("0.1.0") && l.Contains("0.2.0")); // the accept line
        Assert.Contains(log, l => l.Contains(steps[0].Description));
        Assert.Contains(log, l => l.Contains("Migrating /home/gitloom/gitloom"));
        Assert.Contains(log, l => l.Contains("vm upgrade result: succeeded") && l.Contains("move"));
    }

    [Fact]
    public async Task Upgrade_LogSink_RecordsTheStrandedResult_WithTheVhdxPath()
    {
        var orchestrator = new FakeOrchestrator
        {
            Result = new VmUpgradeResult(
                false, VmUpgradeFailureKind.StrandedAfterRetire,
                @"stranded — data at 'C:\data\vm\ext4.vhdx'", @"C:\data\vm\ext4.vhdx",
                PromoteStrategy: "copy-then-cleanup"),
        };
        var log = new List<string>();
        var vm = new VmUpgradeOfferViewModel(orchestrator, Options, "0.1.0", "0.2.0") { LogSink = log.Add };

        await RunOnImmediateContextAsync(() => vm.UpgradeCommand.ExecuteAsync(null));

        // The field gap this closes: the stranded outcome must be diagnosable from the log alone.
        Assert.Contains(log, l =>
            l.Contains("vm upgrade result: failed (StrandedAfterRetire)")
            && l.Contains(@"C:\data\vm\ext4.vhdx")
            && l.Contains("copy-then-cleanup"));
    }

    [Fact]
    public async Task Upgrade_AnUnexpectedOrchestratorThrow_IsSurfaced_NeverAFakeSuccess()
    {
        var orchestrator = new FakeOrchestrator { Throws = new InvalidOperationException("boom") };
        var vm = new VmUpgradeOfferViewModel(orchestrator, Options, "0.1.0", "0.2.0");

        await RunOnImmediateContextAsync(() => vm.UpgradeCommand.ExecuteAsync(null));

        Assert.True(vm.HasError);
        Assert.Contains("boom", vm.ErrorMessage);
        Assert.False(vm.IsComplete);
        Assert.False(vm.IsRunning);
    }

    /// <summary>Runs the command under a synchronization context that executes posts inline, so
    /// <see cref="Progress{T}"/> callbacks land deterministically before the task completes.</summary>
    private static async Task RunOnImmediateContextAsync(Func<Task> action)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new ImmediateSynchronizationContext());
        try
        {
            await action();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private sealed class ImmediateSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class FakeOrchestrator : IVmUpgradeOrchestrator
    {
        public VmUpgradeOptions? ReceivedOptions { get; private set; }
        public VmUpgradeResult Result { get; set; } =
            new(true, VmUpgradeFailureKind.None, "upgraded");
        public Action<IProgress<string>?>? OnRun { get; set; }
        public Exception? Throws { get; set; }

        public Task<VmUpgradeResult> UpgradeAsync(
            VmUpgradeOptions options, IProgress<string>? progress, CancellationToken ct)
        {
            ReceivedOptions = options;
            if (Throws is not null)
                throw Throws;
            OnRun?.Invoke(progress);
            return Task.FromResult(Result);
        }
    }
}
