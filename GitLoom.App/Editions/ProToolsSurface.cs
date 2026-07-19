using System.Threading.Tasks;
using Avalonia.Controls;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;

namespace GitLoom.App.Editions;

/// <summary>
/// The Pro edition's implementation of the shell's <see cref="IProToolsSurface"/> (step 1c). The five
/// agent-platform Tools commands that used to live inline in the SHARED
/// <see cref="ViewModels.RepoDashboardViewModel"/> moved here VERBATIM so the git-workspace hub carries
/// ZERO reference to <c>Mainguard.Agents.Agents.*</c> or any Pro-only View. This is where those references
/// now live. Kept in its own file: in Phase 2 this type moves to the Pro-only UI assembly (its
/// Mainguard.Agents.Agents + Pro-View dependencies belong there); the <see cref="IProToolsSurface"/> contract stays
/// in the shared shell. Behavior under Pro is byte-for-behavior identical to the pre-1c inline bodies.
/// </summary>
public sealed class ProToolsSurface : IProToolsSurface
{
    // AI Providers (T-14 vault): the API-key settings dialog, parented to the shell's MainWindow.
    public async Task ManageAiProvidersAsync(Window owner)
    {
        var dialog = new ApiKeySettingsView { DataContext = new ApiKeySettingsViewModel() };
        await dialog.ShowDialog(owner);
    }

    // Agent CLIs (P2-22 §J-5): the "add more later" surface over the same pinned channel the OOBE
    // picker installs from. A CLI installed here reaches every NEW agent sandbox immediately via the
    // read-only adapters mount — no image rebuild, no re-setup.
    public async Task ManageAgentClisAsync(Window owner)
    {
        var installer = Mainguard.Agents.Agents.Adapters.AgentCliInstaller.CreateDefault(
            new Mainguard.Agents.Agents.Bootstrap.WslRunner());
        var dialog = new AgentCliSettingsView { DataContext = new AgentCliSettingsViewModel(installer) };
        await dialog.ShowDialog(owner);
    }

    // Daemon logs (in-depth per-subsystem logging): the read-only "recent daemon logs" surface over
    // Core's DaemonLogReader (journalctl / tail over the same WSL seam the OOBE health card uses). A
    // CLI installed or a spawn that failed leaves a trace here — no re-setup, no DI (the WslRunner
    // seam is constructed directly, following the Agent-CLIs pattern above).
    public async Task ViewDaemonLogsAsync(Window owner)
    {
        var reader = new Mainguard.Agents.Agents.Bootstrap.DaemonLogReader(
            new Mainguard.Agents.Agents.Bootstrap.WslRunner());
        var dialog = new DaemonLogsView { DataContext = new DaemonLogsViewModel(reader) };
        await dialog.ShowDialog(owner);
    }

    // Tools → Rebuild sandbox images (Item 1 repair action): the user-triggered recovery when startup
    // auto-provisioning keeps failing — force-reprovision EVERY jail image (docker load the bundled CI
    // tar, else build from the bundled source) over the SAME SandboxImageAutoProvision path, with a
    // live oobe.log trace and the shell outcome toast the startup flow uses ("Sandbox images updated." /
    // "…didn't complete"). Fire-and-forget so the (minutes-long) rebuild never blocks the UI. The toast
    // targets the shell if it is present; the rebuild fires regardless (matching the pre-1c body exactly).
    public Task RebuildSandboxImagesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow?.DataContext is MainWindowViewModel shell)
        {
            shell.ShowToast("Rebuilding sandbox images…", isError: false);
        }

        // Per-step build/load lines go to oobe.log; the final Installed/Updated/InstallFailed toast is
        // published by the installer's outcome sink (single-sourced with the startup path).
        var progress = new System.Progress<string>(line => App.LogOobe($"sandbox images (rebuild): {line}"));
        _ = Task.Run(() =>
            Services.SandboxImageInstaller.RunAsync(App.LogOobe, progress, force: true));
        return Task.CompletedTask;
    }

    // Add Repos to Mainguard OS (PR2 follow-up): the post-setup surface over the SAME repo-onboarding
    // engine the OOBE step drives — for a user who skipped that step (or whose copies failed) and
    // wants agent-ready copies now, without opening each repository once. The VM is composed by
    // App.CreateAddReposToOsViewModel (pickers parent to the dialog; provision pipeline + repo
    // store are the OOBE's own seams).
    public async Task AddReposToOsAsync(Window owner)
    {
        var dialog = new AddReposToOsView();
        dialog.DataContext = App.CreateAddReposToOsViewModel(dialog);
        await dialog.ShowDialog(owner);
    }
}
