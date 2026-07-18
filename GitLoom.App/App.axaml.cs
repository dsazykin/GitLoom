using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
            rebootHasCompleted: static (since, _) => Task.FromResult(SystemRebootEvidence.RebootedSince(since)),
            // The repo-onboarding step (PR2): the existing auto-detect discovery walk, the OOBE
            // window's own storage-provider pickers, the P2-06 provision+register pipeline, and the
            // sidebar's one repo store. Failure there can never fail the OOBE — the step is skippable.
            repoDiscovery: new RepoDiscoveryService(new GitService()),
            pickRepoRootFolder: static () => PickOobeFolderAsync("Select the folder that contains your repositories"),
            pickIndividualRepoFolders: static () => PickOobeFoldersAsync("Select the repositories to copy into GitLoom OS"),
            provisionRepo: static (path, ct) => ProvisionRepoIntoOsAsync(path, ct),
            persistRepo: static path => RepoCatalog.EnsureRegistered(path),
            settingsService: Settings);
    }

    /// <summary>
    /// Builds the post-setup "Add Repos to GitLoom OS" window VM (Tools → Add Repos to GitLoom OS…) —
    /// the SAME onboarding engine and per-repo pipeline the OOBE repo step drives (the existing
    /// auto-detect discovery walk, <see cref="ProvisionRepoIntoOsAsync"/>, the sidebar's one repo
    /// store), so a user who skipped that step (or whose copies failed) adds repositories later
    /// without opening each one once. Constructed directly (static-<c>Settings</c> pattern, no DI).
    /// The folder pickers parent to the dialog itself — its owner is modal-disabled while it shows.
    /// </summary>
    public static AddReposToOsViewModel CreateAddReposToOsViewModel(Window pickerOwner)
        => new(
            new RepoDiscoveryService(new GitService()),
            () => PickFolderAsync(pickerOwner, "Select the folder that contains your repositories"),
            () => PickFoldersAsync(pickerOwner, "Select the repositories to copy into GitLoom OS"),
            static (path, ct) => ProvisionRepoIntoOsAsync(path, ct),
            static path => RepoCatalog.EnsureRegistered(path),
            Settings);

    /// <summary>Single-folder picker over the current top-level window (the OOBE wizard while it is
    /// the app's MainWindow). Returns null when dismissed.</summary>
    private static async Task<string?> PickOobeFolderAsync(string title)
    {
        var picked = await PickOobeFoldersAsync(title, allowMultiple: false);
        return picked.Count > 0 ? picked[0] : null;
    }

    /// <summary>Multi-folder picker over the current top-level window. Empty when dismissed.</summary>
    private static Task<IReadOnlyList<string>> PickOobeFoldersAsync(string title)
        => PickOobeFoldersAsync(title, allowMultiple: true);

    private static Task<IReadOnlyList<string>> PickOobeFoldersAsync(string title, bool allowMultiple)
    {
        if (Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }

        return PickFoldersCoreAsync(desktop.MainWindow, title, allowMultiple);
    }

    /// <summary>Single-folder picker over an explicit owner window. Returns null when dismissed.</summary>
    private static async Task<string?> PickFolderAsync(Window owner, string title)
    {
        var picked = await PickFoldersCoreAsync(owner, title, allowMultiple: false);
        return picked.Count > 0 ? picked[0] : null;
    }

    /// <summary>Multi-folder picker over an explicit owner window. Empty when dismissed.</summary>
    private static Task<IReadOnlyList<string>> PickFoldersAsync(Window owner, string title)
        => PickFoldersCoreAsync(owner, title, allowMultiple: true);

    private static async Task<IReadOnlyList<string>> PickFoldersCoreAsync(Window owner, string title, bool allowMultiple)
    {
        var result = await owner.StorageProvider.OpenFolderPickerAsync(
            new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = title, AllowMultiple = allowMultiple });
        return result.Select(f => f.Path.LocalPath).ToList();
    }

    /// <summary>The per-repo onboarding pipeline: the SAME provision+register flow the app runs when a
    /// repo is opened (P2-06 <c>TryRegisterSyncRemoteAsync</c>) — <c>ProvisionRepo</c> over the one
    /// gRPC touch-point, then the daemon-resolved sync remote registered idempotently (never a
    /// hardcoded name). A generous deadline: a mirror clone of a large repo is minutes, not the
    /// default 10 s.</summary>
    private static async Task ProvisionRepoIntoOsAsync(string repoPath, CancellationToken ct)
    {
        using var daemon = DaemonClient.ForLoopback();
        var provisioned = await daemon
            .ProvisionRepoAsync(repoPath, ct, deadline: TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        new SyncRemoteRegistrar(new GitService())
            .Register(repoPath, provisioned.SyncRemoteName, provisioned.SyncRemoteUrl);
    }

    /// <summary>The exe the resume Scheduled Task must point at — the running app itself.</summary>
    private static string ResumeTargetExePath()
        => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "GitLoom.App.exe");

    /// <summary>Best-effort line into <c>%LocalAppData%\GitLoom\oobe.log</c> — provisioning-lifecycle
    /// breadcrumbs (resume-task sweeps and launch routing) so a misbehaving setup leaves a trace.
    /// Internal so the Tools → Rebuild sandbox images action shares the one breadcrumb sink.</summary>
    internal static void LogOobe(string message)
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
    private static int _fullExitStarted;
    private TrayIcon? _trayIcon;

    /// <summary>Holds GitLoomEnv awake while the app runs (see <see cref="GitLoom.Core.Agents.Bootstrap.VmKeepAlive"/>);
    /// released on exit before the optional VM stop. Static (one App instance) so the static exit
    /// paths — the visualized shutdown and the framework Exit backstop — both reach it.</summary>
    private static GitLoom.Core.Agents.Bootstrap.VmKeepAlive? _vmKeepAlive;

    /// <summary>Starts the VM keep-alive holder once (idempotent). Called from the first moment on
    /// BOTH launch routes — the OOBE wizard's own gRPC steps need the VM held just as much as the
    /// control center — and as the startup sequence's first action.</summary>
    private static void EnsureKeepAlive() =>
        _vmKeepAlive ??= new GitLoom.Core.Agents.Bootstrap.VmKeepAlive();

    /// <summary>Releases the keep-alive holder (idempotent) so the optional VM stop isn't fighting a
    /// holder that would reboot it. Safe to call from both exit paths.</summary>
    private static void ReleaseKeepAlive()
    {
        _vmKeepAlive?.Dispose();
        _vmKeepAlive = null;
    }

    /// <summary>The one full-exit path: marks the exit (so close-to-tray interception stands down),
    /// then runs the VISUALIZED shutdown (release keep-alive, optional VM stop) to completion before
    /// shutting the desktop lifetime down. Reentrancy-guarded: a second exit request is ignored so
    /// the teardown never double-runs.</summary>
    public static void RequestFullExit()
    {
        if (System.Threading.Interlocked.Exchange(ref _fullExitStarted, 1) != 0)
        {
            return;
        }

        IsExiting = true;
        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _ = RunVisualizedShutdownThenExitAsync(desktop);
        }
    }

    /// <summary>Shows the shutdown window, runs <see cref="GitLoom.Core.Agents.Bootstrap.AppShutdownSequence"/>
    /// (release keep-alive, and — when StopVmOnExit is on — stop GitLoom OS with completion), then
    /// shuts the lifetime down. The framework Exit hook remains a backstop; its release/stop are
    /// no-ops here (both are idempotent-guarded), so nothing double-runs.</summary>
    private static async Task RunVisualizedShutdownThenExitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var vm = new ViewModels.ShutdownWindowViewModel();
            var window = new Views.ShutdownWindow { DataContext = vm };
            desktop.MainWindow = window;
            window.Show();

            var env = new Services.ProductionShutdownEnvironment(ReleaseKeepAlive, StopVmScopedAsync, LogOobe);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await new GitLoom.Core.Agents.Bootstrap.AppShutdownSequence(env)
                .RunAsync(vm, cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogOobe($"visualized shutdown failed (proceeding to exit): {ex.Message}");
        }
        finally
        {
            desktop.Shutdown();
        }
    }

    /// <summary>Live-agent count for the exit guard (wired by MainWindowViewModel; null = unknown = 0).</summary>
    public static Func<int>? LiveAgentCountProvider { get; set; }

    /// <summary>The exit guard's confirmation seam (overridable in tests; defaults to the shared dialog).</summary>
    public static Services.IConfirmationService ExitConfirmation { get; set; } = new Services.DialogConfirmationService();

    /// <summary>
    /// The guarded full exit every user-facing exit path calls (tray Exit, File → Exit, the X with
    /// close-to-tray off): when the exit would stop the VM under live agents
    /// (<see cref="Services.VmExitGuard"/>), it confirms first — the main window is shown so the
    /// dialog has an owner (the tray path may fire with it hidden). Declining leaves everything
    /// running; confirming (or no live agents / VM kept) proceeds to <see cref="RequestFullExit"/>.
    /// </summary>
    public static async Task RequestFullExitGuardedAsync()
    {
        try
        {
            var liveAgents = LiveAgentCountProvider?.Invoke() ?? 0;
            if (Services.VmExitGuard.ShouldConfirm(Settings.Current.StopVmOnExit, liveAgents))
            {
                if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    ShowMainWindow(desktop);
                }

                var confirmed = await ExitConfirmation.ConfirmAsync(
                    Services.VmExitGuard.Title,
                    Services.VmExitGuard.Message(liveAgents),
                    Services.VmExitGuard.ConfirmButtonText);
                if (!confirmed)
                {
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // The guard must never be able to trap the user in the app.
            LogOobe($"exit guard failed (proceeding to exit): {ex.Message}");
        }

        RequestFullExit();
    }

    /// <summary>Best-effort, once-only scoped stop of the GitLoomEnv VM (`wsl --terminate GitLoomEnv`
    /// ONLY — never the VM-wide shutdown verb, G-12; personal distros are untouched). The
    /// <see cref="_vmStopRan"/> guard is what lets the visualized shutdown and the framework Exit
    /// backstop both call this without the terminate ever running twice. Bounded so a wedged wsl.exe
    /// cannot hang process exit.</summary>
    private static async Task StopVmScopedAsync(CancellationToken ct)
    {
        if (System.Threading.Interlocked.Exchange(ref _vmStopRan, 1) != 0)
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await new WslRunner().RunAsync(WslCommands.Terminate(), stdin: null, timeout.Token).ConfigureAwait(false);
            LogOobe("terminated GitLoomEnv on exit (StopVmOnExit)");
        }
        catch (Exception ex)
        {
            LogOobe($"stop-VM-on-exit failed (non-fatal): {ex.Message}");
        }
    }

    /// <summary>The framework Exit backstop's synchronous VM stop: honors StopVmOnExit, then runs the
    /// guarded scoped terminate. After the visualized shutdown already ran, the guard makes this a
    /// no-op.</summary>
    private static void StopVmOnExitBestEffort()
    {
        if (!Settings.Current.StopVmOnExit)
            return;

        StopVmScopedAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    /// <summary>The always-present tray icon: left-click / "Open GitLoom" re-shows the main window
    /// (the X hides to here when CloseToTray is on); "Exit GitLoom" is the full exit.</summary>
    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var open = new Avalonia.Controls.NativeMenuItem("Open GitLoom");
        open.Click += (_, _) => ShowMainWindow(desktop);
        var exit = new Avalonia.Controls.NativeMenuItem("Exit GitLoom");
        exit.Click += (_, _) => _ = RequestFullExitGuardedAsync();

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
            // Hold GitLoomEnv awake for the app's lifetime. WSL idle-terminates the distro seconds
            // after the last wsl.exe client exits (gRPC connections don't count), taking gitloomd
            // down between RPCs — waking it (in the startup sequence) is useless without a holder.
            // Started from the FIRST moment on BOTH routes: the OOBE wizard's own gRPC steps (repo
            // onboarding) need the VM held just as much as the control center; before the distro
            // exists the holder just retries quietly. The control-center route's startup sequence
            // re-ensures this as its first action (idempotent).
            EnsureKeepAlive();

            // Full exit (any path — tray Exit, File > Exit, X with CloseToTray off) is now the
            // visualized shutdown (RequestFullExit); this framework Exit hook is the BACKSTOP for a
            // shutdown that bypassed it (OS logoff). Both release the keep-alive FIRST so the stop
            // isn't fighting a holder that would reboot it; both are idempotent-guarded, so whichever
            // ran first wins and this never double-runs. Hiding to the tray never triggers any of it.
            desktop.Exit += (_, _) =>
            {
                ReleaseKeepAlive();
                StopVmOnExitBestEffort();
            };
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

            // OOBE is its own sequence (the wizard); the control-center route runs the startup
            // sequence behind the loading screen — VM wake, daemon reachable, tier-1 refresh, and the
            // consented tier-2 upgrade all complete (or degrade with a banner) BEFORE the shell opens,
            // subsuming the old fire-and-forget WakeVm/RefreshDaemon block entirely.
            desktop.MainWindow = route == LaunchRoute.Oobe
                ? new OobeWizardView { DataContext = CreateOobeWizardViewModel() }
                : CreateStartupWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>True once the user picked "Later" on this run's VM upgrade offer — don't nag again
    /// this session (in-memory only; the next launch re-offers).</summary>
    private static bool _vmUpgradeDeclinedThisSession;

    /// <summary>Builds the control-center loading screen: wires the production startup environment to
    /// a <see cref="GitLoom.Core.Agents.Bootstrap.AppStartupSequence"/> and returns the
    /// <see cref="Views.StartupWindow"/> that drives it and swaps to MainWindow on completion. The
    /// design-render harness bypasses this by constructing the ViewModel directly (no SequenceRunner),
    /// like the OrchestratorServicesFactory seam.</summary>
    private static Views.StartupWindow CreateStartupWindow()
    {
        var vm = new ViewModels.StartupWindowViewModel();
        var env = new Services.ProductionStartupEnvironment(EnsureKeepAlive, LogOobe)
        {
            Host = vm,
            VmUpgradeDeclinedThisSession = _vmUpgradeDeclinedThisSession,
        };
        var sequence = new GitLoom.Core.Agents.Bootstrap.AppStartupSequence(env);
        vm.SequenceRunner = (progress, ct) => RunStartupSequenceAsync(sequence, env, progress, ct);
        return new Views.StartupWindow { DataContext = vm };
    }

    private static async Task<GitLoom.Core.Agents.Bootstrap.StartupResult> RunStartupSequenceAsync(
        GitLoom.Core.Agents.Bootstrap.AppStartupSequence sequence,
        Services.ProductionStartupEnvironment env,
        IProgress<GitLoom.Core.Agents.Bootstrap.StartupProgress> progress,
        CancellationToken ct)
    {
        var result = await sequence.RunAsync(progress, ct).ConfigureAwait(false);
        // Carry the "Later" choice across the loading screen so a later re-entry doesn't re-nag.
        _vmUpgradeDeclinedThisSession = env.VmUpgradeDeclinedThisSession;
        return result;
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
