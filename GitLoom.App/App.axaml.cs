using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
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

    /// <summary>The shipped provisioning probe: GitLoomEnv distro registered + daemon healthy.</summary>
    public static IProvisioningProbe CreateProvisioningProbe()
    {
        var wsl = new WslRunner();
        return new ProvisioningProbe(wsl, new WslDaemonHealthProbe(wsl));
    }

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
        var resumeTarget = Environment.ProcessPath ?? Path.Combine(appDir, "GitLoom.App.exe");
        var helperExe = Path.Combine(appDir, "GitLoom.Installer.Elevated.exe");
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var resultPath = Path.Combine(localAppData, "GitLoom", "elevated-result.json");
        var launcher = new RunAsElevationLauncher(helperExe, resumeTarget, resultPath);

        var options = new BootstrapOptions(
            InstallDir: Path.Combine(localAppData, "GitLoom", "vm"),
            TarballPath: Path.Combine(appDir, "payload", "GitLoomOS.tar.gz"));
        var ctx = new BootstrapContext(wsl, new BootstrapFileSystem(), new WslDaemonHealthProbe(wsl), options);
        var bootstrapper = GitLoomOsBootstrapper.Create(ctx);

        return new OobeWizardViewModel(machine, diagnostics, launcher, bootstrapper);
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Load environment variables securely from .env file
        DotNetEnv.Env.TraversePath().Load();

        // Instantiate and load the settings service
        Settings = new SettingsService();

        // Ensure SQLite database is created and migrations are applied
        using (var dbContext = new AppDbContext())
        {
            dbContext.Database.Migrate();
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Apply the persisted theme (or the default) before any window opens.
        Theming.ThemeManager.Initialize(Settings.Current.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = DecideLaunchRoute() == LaunchRoute.Oobe
                ? new OobeWizardView { DataContext = CreateOobeWizardViewModel() }
                : new MainWindow { DataContext = new MainWindowViewModel() };
        }

        base.OnFrameworkInitializationCompleted();
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
