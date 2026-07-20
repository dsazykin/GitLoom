using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests.Headless;

/// <summary>
/// ViewModel behavior for the startup loading screen + the shutdown window + the MainWindow degraded
/// banner. [AvaloniaFact] because the progress sinks marshal through the shared headless dispatcher.
/// </summary>
public class StartupShutdownViewModelTests
{
    private static void Drain()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    [AvaloniaFact]
    public void Startup_progress_maps_onto_the_matching_checklist_row_and_status_line()
    {
        var vm = new StartupWindowViewModel();
        Assert.Equal(4, vm.Stages.Count);

        ((IProgress<StartupProgress>)vm).Report(
            new StartupProgress(StartupStage.ConnectDaemon, BootstrapStageState.Running, StartupStatus.ConnectingDaemon));
        Drain();

        Assert.Equal(StartupStatus.ConnectingDaemon, vm.StatusText);
        Assert.True(vm.Stages[(int)StartupStage.ConnectDaemon].IsRunning);
        Assert.True(vm.Stages[(int)StartupStage.PrepareEnvironment].IsPending);
    }

    [AvaloniaFact]
    public void Startup_failed_stage_flips_degraded()
    {
        var vm = new StartupWindowViewModel();

        ((IProgress<StartupProgress>)vm).Report(
            new StartupProgress(StartupStage.ConnectDaemon, BootstrapStageState.Failed, StartupStatus.DaemonUnreachableStatus));
        Drain();

        Assert.True(vm.IsDegraded);
        Assert.True(vm.Stages[(int)StartupStage.ConnectDaemon].IsFailed);
        Assert.Equal(StartupStatus.DaemonUnreachableStatus, vm.StatusText);
    }

    [AvaloniaFact]
    public void Startup_hosts_and_tears_down_the_tier2_upgrade_offer()
    {
        var vm = new StartupWindowViewModel();
        Assert.False(vm.IsUpgrading);

        var offer = new VmUpgradeOfferViewModel("0.2.0", "0.3.0");
        vm.BeginVmUpgrade(offer);
        Assert.True(vm.IsUpgrading);
        Assert.Same(offer, vm.PendingUpgrade);
        Assert.Equal(StartupStatus.UpgradingOs, vm.StatusText);

        vm.EndVmUpgrade();
        Assert.False(vm.IsUpgrading);
        Assert.Null(vm.PendingUpgrade);
    }

    [AvaloniaFact]
    public async Task Startup_StartAsync_runs_the_sequence_and_raises_Completed_with_the_result()
    {
        var vm = new StartupWindowViewModel();
        var expected = new StartupResult(false, StartupStatus.DaemonUnreachableBanner);
        vm.SequenceRunner = (_, _) => Task.FromResult(expected);

        StartupResult? got = null;
        vm.Completed += (_, r) => got = r;

        await vm.StartAsync();
        Drain();

        Assert.Equal(expected, got);
    }

    [AvaloniaFact]
    public void Shutdown_report_updates_the_status_line()
    {
        var vm = new ShutdownWindowViewModel();
        Assert.Equal(ShutdownStatus.ReleasingKeepAlive, vm.StatusText);

        ((IProgress<string>)vm).Report(ShutdownStatus.StoppingVm);
        Drain();

        Assert.Equal(ShutdownStatus.StoppingVm, vm.StatusText);
    }

    [AvaloniaFact]
    public void MainWindow_carries_the_degraded_banner_and_clears_it_when_set_null()
    {
        var vm = new MainWindowViewModel(new StartupResult(false, StartupStatus.DaemonUnreachableBanner).DegradedBanner);
        try
        {
            Assert.True(vm.HasStartupBanner);
            Assert.Equal(StartupStatus.DaemonUnreachableBanner, vm.StartupBanner);

            // The daemon-reachable clear path sets this null (see OnDaemonReachable).
            vm.StartupBanner = null;
            Assert.False(vm.HasStartupBanner);
        }
        finally
        {
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void MainWindow_ready_result_shows_no_banner()
    {
        var vm = new MainWindowViewModel(StartupResult.Ready.DegradedBanner);
        try
        {
            Assert.False(vm.HasStartupBanner);
            Assert.Null(vm.StartupBanner);
        }
        finally
        {
            vm.Dispose();
        }
    }
}
