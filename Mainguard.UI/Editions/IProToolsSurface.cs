using System.Threading.Tasks;
using Avalonia.Controls;

namespace Mainguard.UI.Editions;

/// <summary>
/// The Tools-menu surface the SHARED git-workspace hub (<c>ViewModels.RepoDashboardViewModel</c>)
/// talks to instead of naming the Pro agent-platform types directly (step 1c) — so the hub carries ZERO
/// reference to <c>Mainguard.Agents.Agents.*</c> or any Pro-only View, and the shell compiles without the Pro
/// UI assembly (the boundary Phase 2 depends on). It exposes EXACTLY the five agent-platform Tools
/// operations; every signature uses shell-only types (an Avalonia <see cref="Window"/> dialog owner,
/// primitives) — NO Mainguard.Agents.Agents type leaks across the seam. <c>ProToolsSurface</c> satisfies it
/// under the Pro edition; the Client manifest returns <c>null</c> for <see cref="IEditionManifest.ProTools"/>,
/// so each hub command no-ops (and the menu items are gated off by <c>MainWindowViewModel.HasAgentPlatform</c>).
/// </summary>
public interface IProToolsSurface
{
    /// <summary>Tools → AI Providers…: open the API-key vault dialog, parented to <paramref name="owner"/>.</summary>
    Task ManageAiProvidersAsync(Window owner);

    /// <summary>Tools → Agent CLIs…: open the agent-CLI install/manage dialog, parented to <paramref name="owner"/>.</summary>
    Task ManageAgentClisAsync(Window owner);

    /// <summary>Tools → Daemon logs…: open the read-only recent-daemon-logs dialog, parented to <paramref name="owner"/>.</summary>
    Task ViewDaemonLogsAsync(Window owner);

    /// <summary>Tools → Rebuild sandbox images…: force-reprovision every jail image (fire-and-forget, with
    /// the shell toast + oobe.log trace). Self-resolves its shell/toast target, so it needs no owner.</summary>
    Task RebuildSandboxImagesAsync();

    /// <summary>Tools → Add Repos to Mainguard OS…: open the post-setup repo-onboarding dialog, parented to
    /// <paramref name="owner"/>.</summary>
    Task AddReposToOsAsync(Window owner);
}
