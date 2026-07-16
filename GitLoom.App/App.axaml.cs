using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using GitLoom.App.Services;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Bootstrap;
using GitLoom.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace GitLoom.App;

public partial class App : Application
{
    public static ISettingsService Settings { get; private set; } = null!;

    /// <summary>
    /// P2-47 — the single composition seam for the control-center's orchestration services. The shipped
    /// app resolves the DaemonClient-backed bundle (<see cref="CreateProductionOrchestratorServices"/>);
    /// the headless design-render harness overrides this to inject a scripted <c>MockOrchestrator</c>
    /// (representative data, explicitly outside the shipped path). Follows the existing static-<c>Settings</c>
    /// pattern rather than adding a DI container to the App.
    /// </summary>
    public static Func<OrchestratorServices> OrchestratorServicesFactory { get; set; }
        = CreateProductionOrchestratorServices;

    /// <summary>The shipped control-center services: real DaemonClient-backed, no mock (P2-47).</summary>
    public static OrchestratorServices CreateProductionOrchestratorServices()
        => DaemonBackedOrchestrator.CreateBundle();

    /// <summary>The bundle the app's control center runs on — the factory's current value.</summary>
    public static OrchestratorServices CreateOrchestratorServices() => OrchestratorServicesFactory();

    /// <summary>
    /// P2-48 launch-routing seam: the provisioning probe the single entry point consults on startup to
    /// decide OOBE-vs-control-center. Defaults to the real WSL/daemon probe; overridable (tests/dev).
    /// Follows the static-<c>Settings</c>/factory pattern (no DI container).
    /// </summary>
    public static Func<IProvisioningProbe> ProvisioningProbeFactory { get; set; } = CreateProvisioningProbe;

    /// <summary>The shipped provisioning probe (audit fix #8): GitLoomEnv registered AND no OOBE run
    /// mid-flight — an installed-state question that never cold-boots the VM inside the startup
    /// budget. Daemon liveness is the control center's job (reconnect + Degraded state), not the
    /// router's; demanding it here mis-routed provisioned machines with an idle-stopped VM back
    /// into the wizard.</summary>
    public static IProvisioningProbe CreateProvisioningProbe()
        => new InstalledStateProbe(
            new WslRunner(),
            static () => new OobeStateMachine(new JsonOobeStateStore(JsonOobeStateStore.DefaultPath())).CurrentStage);

    /// <summary>Builds the in-app OOBE wizard VM over P2-21's tested machinery (same state machine as
    /// the console driver). The elevated helper + payload are resolved from the app's own directory,
    /// where the packaged build co-locates them.</summary>
    public static OobeWizardViewModel CreateOobeWizardViewModel()
    {
        var wsl = new WslRunner();
        var store = new JsonOobeStateStore(JsonOobeStateStore.DefaultPath());
        var machine = new OobeStateMachine(store);
        var diagnostics = new SystemDiagnostics(new WindowsSystemProbe(), new WslStatusProbe(wsl));

        var appDir = AppContext.BaseDirectory;
        // The reboot-resume Scheduled Task relaunches THIS gui app back into the wizard (not a console).
        var resumeTarget = ResumeTargetExePath();
        var helperExe = Path.Combine(appDir, "GitLoom.Installer.Elevated.exe");
        var dataRoot = GitLoomPaths.DataRoot();
        var resultPath = Path.Combine(dataRoot, "elevated-result.json");
        var launcher = new RunAsElevationLauncher(helperExe, resumeTarget, resultPath);

        var options = new BootstrapOptions(
            InstallDir: Path.Combine(dataRoot, "vm"),
            TarballPath: Path.Combine(appDir, "payload", "GitLoomOS.tar.gz"));
        // End-to-end health (audit fix #9): the OOBE's final gate is an AUTHENTICATED gRPC call from
        // THIS app over loopback — process-existence alone shipped a "Done" the control center then
        // couldn't talk to (the token never crossed the VM boundary).
        var ctx = new BootstrapContext(wsl, new BootstrapFileSystem(), new EndToEndDaemonHealthProbe(wsl), options);
        var bootstrapper = GitLoomOsBootstrapper.Create(ctx);

        // Anti-zombie hygiene (see ResumeTaskGuard): after every pass that does not hand off to a
        // reboot, delete the elevated ONLOGON resume task; plus the cross-process single-instance
        // lock so this wizard and a task-relaunched wizard can never drive one machine concurrently.
        var guard = new ResumeTaskGuard(log: LogOobe);
        void Sweep() => guard.Sweep(machine.CurrentStage, launchedByResumeTask: false, resumeTarget);

        // Whether GitLoomEnv is STILL registered — the same check ImportDistroStep uses. The machine
        // consults this on resume so a persisted "VM imported" flag that has gone stale (the user
        // unregistered the distro between runs) rewinds to a fresh import rather than sailing into the
        // agent-CLI step against a VM that no longer exists.
        async Task<bool> VmIsRegistered(CancellationToken ct)
        {
            var list = await wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
            return WslRunner.ParseDistroList(list.StdOut)
                .Any(d => string.Equals(d, WslCommands.DistroName, StringComparison.OrdinalIgnoreCase));
        }

        return new OobeWizardViewModel(
            machine, diagnostics, launcher, bootstrapper,
            resumeTaskSweep: Sweep,
            instanceLockFactory: static () => OobeInstanceLock.TryAcquire(),
            // The agent-CLI picker step (P2-22 §J-5): the bundled pinned channel installing into the
            // freshly provisioned VM. Failure there can never fail the OOBE — the step is skippable.
            cliInstaller: GitLoom.Core.Agents.Adapters.AgentCliInstaller.CreateDefault(wsl),
            vmIsRegistered: VmIsRegistered,
            // Fix #4: a relaunch BEFORE the restart re-shows the restart panel instead of importing
            // onto half-enabled Windows features (boot time vs. the RebootPending stamp).
            rebootHasCompleted: static (since, _) => Task.FromResult(SystemRebootEvidence.RebootedSince(since)));
    }

    /// <summary>The exe the resume Scheduled Task must point at — the running app itself.</summary>
    private static string ResumeTargetExePath()
        => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GitLoom.App.exe");

    /// <summary>Best-effort line into <c>%LocalAppData%\GitLoom\oobe.log</c> — provisioning-lifecycle
    /// breadcrumbs (resume-task sweeps and launch routing) so a misbehaving setup leaves a trace.</summary>
    private static void LogOobe(string message)
    {
        try
        {
            var dir = GitLoomPaths.DataRoot();
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "oobe.log"),
                $"{DateTimeOffset.UtcNow:O} [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never break the flow they diagnose.
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Load environment variables securely from .env file
        DotNetEnv.Env.TraversePath().Load();

        // Instantiate and load the settings service
        Settings = new SettingsService();

        // Ensure SQLite database is created and migrations are applied. This runs before any window
        // exists, so a bare Migrate() that blocks on a locked database would leave a windowless,
        // dead-looking process (see the single-instance guard in Program.cs). Bound it: if the DB is
        // held by something else we fail loudly and fast instead of hanging invisibly forever.
        try
        {
            var migration = System.Threading.Tasks.Task.Run(() =>
            {
                using var dbContext = new AppDbContext();
                dbContext.Database.Migrate();
            });

            if (!migration.Wait(TimeSpan.FromSeconds(20)))
            {
                throw new TimeoutException(
                    "Timed out applying database migrations. Another GitLoom instance may be holding "
                    + "the database lock — close it and relaunch.");
            }

            // Re-throw any exception the migration task captured on this thread.
            migration.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // Better a crash with a reason than a silent, windowless hang. LogToTrace / the console
            // will carry this, and the process exits with a non-zero code instead of lingering.
            Console.Error.WriteLine($"[GitLoom] Fatal: database migration failed. {ex.Message}");
            throw;
        }
    }

    // ---- App lifecycle: tray + full exit + stop-VM-on-exit (user setting, defaults on) ----

    /// <summary>True once a FULL exit is underway (tray menu / File > Exit / CloseToTray off) — the
    /// signal MainWindow's close interception uses to let the window actually close.</summary>
    public static bool IsExiting { get; private set; }

    private static int _vmStopRan;
    private TrayIcon? _trayIcon;

    /// <summary>The one full-exit path: marks the exit (so close-to-tray interception stands down)
    /// and shuts the desktop lifetime down; the lifetime's Exit hook then stops the VM if configured.</summary>
    public static void RequestFullExit()
    {
        IsExiting = true;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Best-effort, once-only stop of the GitLoomEnv VM on full exit (saves the VM's
    /// memory/CPU when GitLoom is not running). Scoped `wsl --terminate GitLoomEnv` ONLY — never the
    /// VM-wide shutdown verb (G-12); personal distros are untouched. Bounded so a wedged wsl.exe
    /// cannot hang process exit.</summary>
    private static void StopVmOnExitBestEffort()
    {
        if (System.Threading.Interlocked.Exchange(ref _vmStopRan, 1) != 0)
            return;

        try
        {
            if (!Settings.Current.StopVmOnExit)
                return;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            new WslRunner().RunAsync(WslCommands.Terminate(), stdin: null, cts.Token).GetAwaiter().GetResult();
            LogOobe("terminated GitLoomEnv on exit (StopVmOnExit)");
        }
        catch (Exception ex)
        {
            LogOobe($"stop-VM-on-exit failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>The always-present tray icon: left-click / "Open GitLoom" re-shows the main window
    /// (the X hides to here when CloseToTray is on); "Exit GitLoom" is the full exit.</summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new Avalonia.Controls.NativeMenuItem("Open GitLoom");
        open.Click += (_, _) => ShowMainWindow(desktop);
        var exit = new Avalonia.Controls.NativeMenuItem("Exit GitLoom");
        exit.Click += (_, _) => RequestFullExit();

        var menu = new Avalonia.Controls.NativeMenu();
        menu.Items.Add(open);
        menu.Items.Add(new Avalonia.Controls.NativeMenuItemSeparator());
        menu.Items.Add(exit);

        _trayIcon = new TrayIcon
        {
            Icon = new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(
                new Uri("avares://GitLoom.App/Assets/avalonia-logo.ico"))),
            ToolTipText = "GitLoom",
            Menu = menu,
        };
        _trayIcon.Clicked += (_, _) => ShowMainWindow(desktop);
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private static void ShowMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (desktop.MainWindow is { } window)
        {
            window.Show();
            window.WindowState = Avalonia.Controls.WindowState.Normal;
            window.Activate();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the persisted theme (or the default) before any window opens.
        Theming.ThemeManager.Initialize(Settings.Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Full exit (any path — tray Exit, File > Exit, X with CloseToTray off) stops the VM
            // when the StopVmOnExit setting is on. Hiding to the tray never triggers this.
            desktop.Exit += (_, _) => StopVmOnExitBestEffort();
            SetupTrayIcon(desktop);

            // FIRST action, before any route decision: resume-task hygiene. A `--resume` launch means
            // the elevated ONLOGON task just fired us — its purpose is served and we are the elevated
            // instance, the one place its deletion can never be denied. Doing this unconditionally
            // (even on the control-center route) kills the worst zombie: a task surviving past a
            // completed install would otherwise re-run setup elevated at EVERY logon, and the wizard
            // that knows how to delete it would never be constructed again.
            var launchedByResumeTask = Environment.GetCommandLineArgs().Contains("--resume");
            SweepResumeTaskAtStartup(launchedByResumeTask);

            var route = DecideLaunchRoute();
            if (route == LaunchRoute.ControlCenter)
            {
                // The routing probe deliberately no longer boots the VM (fix #8) — wake it here in
                // the background instead, so systemd has gitloomd up by the time the user acts. The
                // control center's reconnect machinery covers the seconds in between.
                WakeVmInBackground();
            }

            desktop.MainWindow = route == LaunchRoute.Oobe
                ? new OobeWizardView { DataContext = CreateOobeWizardViewModel() }
                : new MainWindow { DataContext = new MainWindowViewModel() };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Best-effort, fire-and-forget boot of the GitLoomEnv VM (`wsl -d GitLoomEnv true`):
    /// starting any command boots the distro, and systemd then brings gitloomd up on its own. Never
    /// blocks or fails the launch path.</summary>
    private static void WakeVmInBackground()
    {
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                await new WslRunner()
                    .RunAsync(WslCommands.InDistro("true"), stdin: null, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogOobe($"background VM wake failed: {ex.Message}");
            }
        });
    }

    /// <summary>The startup resume-task sweep: self-delete on a <c>--resume</c> fire; otherwise delete
    /// any registration not legitimised by a persisted <c>RebootPending</c> stage (incl. the identity
    /// check for tasks pointing at retired exes from older installs). Best-effort and fast; failures
    /// are logged to <c>oobe.log</c>, never fatal to launch.</summary>
    private static void SweepResumeTaskAtStartup(bool launchedByResumeTask)
    {
        try
        {
            var stage = new OobeStateMachine(new JsonOobeStateStore(JsonOobeStateStore.DefaultPath())).CurrentStage;
            new ResumeTaskGuard(log: LogOobe).Sweep(stage, launchedByResumeTask, ResumeTargetExePath());
        }
        catch (Exception ex)
        {
            LogOobe($"startup resume-task sweep failed: {ex}");
        }
    }

    /// <summary>
    /// P2-48 — the one launch decision: probe whether the runtime is provisioned and route accordingly.
    /// A developer escape hatch (<c>GITLOOM_SKIP_OOBE=1</c> or a <c>--control-center</c>/<c>--no-oobe</c>
    /// argument) forces the control center so a source/dev run never hits provisioning setup. The probe
    /// runs on a pool thread with a timeout (never deadlocks the startup thread); any fault or timeout
    /// falls back to OOBE (show setup rather than a broken control center).
    /// </summary>
    private static LaunchRoute DecideLaunchRoute()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("GITLOOM_SKIP_OOBE"), "1", StringComparison.Ordinal)
            || Environment.GetCommandLineArgs().Any(a => a is "--control-center" or "--no-oobe"))
            return LaunchRoute.ControlCenter;

        try
        {
            return System.Threading.Tasks.Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                return await LaunchRouter.DecideAsync(ProvisioningProbeFactory(), cts.Token).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch
        {
            return LaunchRoute.Oobe;
        }
    }
}
