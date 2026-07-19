using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using GitLoom.App.Services;
using GitLoom.App.ViewModels;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Services;

namespace GitLoom.App.Editions;

/// <summary>
/// The Pro-edition composition seams the moved Pro UI reads, POPULATED BY the GitLoom.App shell at
/// startup (<c>App.WireProComposition</c>). Step 2e physically split the Pro UI into Mainguard.Agents.UI,
/// which must never reference GitLoom.App; the handful of App composition-root capabilities the Pro
/// manifest / Pro Tools / control center used to reach through <c>App.*</c> statics are injected DOWN into
/// this static holder instead — the exact inversion the design system already uses for
/// <c>ThemeManager.PersistKey</c> (the lower layer never reaches UP into the app). Unset defaults are
/// inert (no-op sinks, the production orchestrator factory), so a test or harness that constructs a Pro
/// ViewModel without running <c>App.WireProComposition</c> behaves exactly as it did against the old
/// null-guarded <c>App.*</c> statics.
/// </summary>
public static class ProComposition
{
    // ---- orchestration services (was App.OrchestratorServicesFactory / App.CreateOrchestratorServices) ----

    /// <summary>The single composition seam for the control center's orchestration services. Production
    /// resolves the real <see cref="DaemonBackedOrchestrator"/> bundle; the headless design/render harnesses
    /// override this with a scripted mock BEFORE building the shell. The shell keeps a forwarding
    /// <c>App.OrchestratorServicesFactory</c> property over this, so the existing harness seam is unchanged.</summary>
    public static Func<OrchestratorServices> OrchestratorServicesFactory { get; set; } = CreateProduction;

    /// <summary>The shipped control-center services: real DaemonClient-backed, no mock (P2-47).</summary>
    public static OrchestratorServices CreateProduction() => DaemonBackedOrchestrator.CreateBundle();

    /// <summary>The bundle the control center runs on — the factory's current value.</summary>
    public static OrchestratorServices CreateOrchestratorServices() => OrchestratorServicesFactory();

    // ---- shell capabilities the shell wires at startup (all inert until then) ----

    /// <summary>The app settings service (was <c>App.Settings</c>) — the control center reads/writes its
    /// workspace-layout preset through it. Null until wired (falls back to defaults, as before).</summary>
    public static ISettingsService? Settings { get; set; }

    /// <summary>The <c>oobe.log</c> breadcrumb sink (was <c>App.LogOobe</c>) — shared with the shell so a
    /// Pro Tools action leaves a trace in the one log. No-op until wired.</summary>
    public static Action<string> LogOobe { get; set; } = static _ => { };

    /// <summary>Show a toast on the shell's main window (was <c>MainWindowViewModel.ShowToast</c> resolved
    /// off the desktop lifetime). No-op until wired / when no shell is present.</summary>
    public static Action<string, bool> ShowShellToast { get; set; } = static (_, _) => { };

    /// <summary>Force-reprovision every sandbox jail image (was <c>SandboxImageInstaller.RunAsync</c>) —
    /// (log, progress, force) mirroring that signature. Null until wired.</summary>
    public static Func<Action<string>, IProgress<string>?, bool, Task>? RebuildSandboxImages { get; set; }

    /// <summary>Build the post-setup "Add Repos to Mainguard OS" window VM (was
    /// <c>App.CreateAddReposToOsViewModel</c>), parenting its folder pickers to the given owner. Null until
    /// wired.</summary>
    public static Func<Window, AddReposToOsViewModel>? AddReposToOsFactory { get; set; }

    /// <summary>The shared host-collab rail destinations (Pull requests / Issues / Notifications /
    /// Releases) whose <c>ContentViewModelType</c>s name the shell's own host-collab ViewModels (which
    /// stay in GitLoom.App and this assembly must NOT reference). The shell owns and injects them (see
    /// <c>EditionManifests</c>' static ctor), so <see cref="ProManifest"/> can compose them into its rail
    /// without naming those App types. Empty until wired.</summary>
    public static IReadOnlyList<RailSectionDescriptor> HostRailSections { get; set; } =
        Array.Empty<RailSectionDescriptor>();

    /// <summary>Build the shell's main window carrying the (optional) startup result — was
    /// <c>new MainWindow { DataContext = new MainWindowViewModel(result) }</c>. The Pro OOBE / startup
    /// loaders (which live here) swap the desktop's <c>MainWindow</c> to it on completion; the shell (which
    /// owns those types) wires this. Null until wired.</summary>
    public static Func<StartupResult?, Window>? CreateShellWindow { get; set; }
}
