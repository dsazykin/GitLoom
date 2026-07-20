using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the startup loading screen (StartupWindow) and the shutdown window (ShutdownWindow)
// offscreen (headless Skia) in ALL FIVE THEMES across the key states — early mid-sequence loading
// with its status text, the tier-2 upgrade consent pause, the tier-2 upgrade running with its step
// checklist, a degraded/failed-step entry, and the shutdown screen (StopVmOnExit on, mid-teardown) —
// plus the degraded-entry MainWindow banner. Non-empty-frame assertions fail the build on a blank
// render. PNGs land in the gitignored artifacts_headless/. Design-system review without a display.
public class StartupShutdownRenderHarness
{
    [AvaloniaFact]
    public void Capture_Startup_AllThemes()
    {
        try
        {
            foreach (var theme in Mainguard.UI.Theming.ThemeManager.Themes)
            {
                Mainguard.UI.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                CaptureStartup(theme.Key, "loading_early", LoadingEarly());
                CaptureStartup(theme.Key, "upgrade_consent", UpgradeConsent());
                CaptureStartup(theme.Key, "upgrade_running", UpgradeRunning());
                CaptureStartup(theme.Key, "degraded", Degraded());

                CaptureShutdown(theme.Key, "stopping_vm",
                    new ShutdownWindowViewModel(ShutdownStatus.StoppingVm));
                CaptureShutdown(theme.Key, "releasing",
                    new ShutdownWindowViewModel(ShutdownStatus.ReleasingKeepAlive));
            }
        }
        finally
        {
            Mainguard.UI.Theming.ThemeManager.Apply(Mainguard.UI.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    // Prepare done, connecting to the daemon (mid-sequence) — the common "early loading" state.
    private static StartupWindowViewModel LoadingEarly() => new(
        new[]
        {
            new BootstrapStageViewModel("Start the Mainguard OS environment", BootstrapStageState.Done),
            new BootstrapStageViewModel("Connect to the Mainguard OS daemon", BootstrapStageState.Running),
            new BootstrapStageViewModel("Apply updates"),
            new BootstrapStageViewModel("Check sandbox images"),
        },
        StartupStatus.ConnectingDaemon);

    // The consented tier-2 offer, hosted inline (Upgrade / Later showing).
    private static StartupWindowViewModel UpgradeConsent() => new(
        UpdatesRunningStages(),
        StartupStatus.CheckingOsUpdate,
        pendingUpgrade: new VmUpgradeOfferViewModel("0.2.0", "0.3.0"));

    // The tier-2 upgrade running with its step checklist mid-flight.
    private static StartupWindowViewModel UpgradeRunning()
    {
        var offer = new VmUpgradeOfferViewModel("0.2.0", "0.3.0")
        {
            IsOffering = false,
            IsRunning = true,
        };
        if (offer.Steps.Count >= 3)
        {
            offer.Steps[0].State = BootstrapStageState.Done;
            offer.Steps[1].State = BootstrapStageState.Running;
            offer.Steps[1].LogTail = "Migrating ~/mainguard (repos + worktrees) from old → staging…";
        }

        return new StartupWindowViewModel(UpdatesRunningStages(), StartupStatus.UpgradingOs, pendingUpgrade: offer);
    }

    // The degraded entry: the daemon step failed within its budget; honest status line.
    private static StartupWindowViewModel Degraded() => new(
        new[]
        {
            new BootstrapStageViewModel("Start the Mainguard OS environment", BootstrapStageState.Done),
            new BootstrapStageViewModel("Connect to the Mainguard OS daemon", BootstrapStageState.Failed),
            new BootstrapStageViewModel("Apply updates"),
            new BootstrapStageViewModel("Check sandbox images"),
        },
        StartupStatus.DaemonUnreachableStatus,
        isDegraded: true);

    private static IEnumerable<BootstrapStageViewModel> UpdatesRunningStages() => new[]
    {
        new BootstrapStageViewModel("Start the Mainguard OS environment", BootstrapStageState.Done),
        new BootstrapStageViewModel("Connect to the Mainguard OS daemon", BootstrapStageState.Done),
        new BootstrapStageViewModel("Apply updates", BootstrapStageState.Running),
        new BootstrapStageViewModel("Check sandbox images"),
    };

    private static void CaptureStartup(string themeKey, string state, StartupWindowViewModel vm)
    {
        var win = new StartupWindow { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"startup_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"startup {themeKey}/{state} PNG is empty");

        HarnessHygiene.Teardown(win);
        Settle();
    }

    private static void CaptureShutdown(string themeKey, string state, ShutdownWindowViewModel vm)
    {
        var win = new ShutdownWindow { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"shutdown_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"shutdown {themeKey}/{state} PNG is empty");

        HarnessHygiene.Teardown(win);
        Settle();
    }

    [AvaloniaFact]
    public void Capture_MainWindow_DegradedBanner_AllThemes()
    {
        try
        {
            foreach (var theme in Mainguard.UI.Theming.ThemeManager.Themes)
            {
                Mainguard.UI.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                var vm = new MainWindowViewModel(new StartupResult(false, StartupStatus.DaemonUnreachableBanner).DegradedBanner);
                Assert.Equal(StartupStatus.DaemonUnreachableBanner, vm.StartupBanner);
                Assert.True(vm.HasStartupBanner);

                var win = new MainWindow { DataContext = vm, Width = 1200, Height = 760 };
                win.Show();
                Settle();

                var frame = win.CaptureRenderedFrame();
                Assert.NotNull(frame);
                var path = Path.Combine(ArtifactsDir(), $"mainwindow_banner_{theme.Key}.png");
                frame!.Save(path);
                Assert.True(new FileInfo(path).Length > 0, $"mainwindow banner {theme.Key} PNG is empty");

                HarnessHygiene.Teardown(win);
                Settle();
            }
        }
        finally
        {
            Mainguard.UI.Theming.ThemeManager.Apply(Mainguard.UI.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
