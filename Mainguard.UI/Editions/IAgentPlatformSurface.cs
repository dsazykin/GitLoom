using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.UI.Editions;

/// <summary>
/// The daemon's answer to provisioning a repo, projected to primitives so the reference-clean shell
/// (which must not name the Pro <c>DaemonClient</c>/<c>ProvisionedRepo</c> types) can register the
/// sync remote with its own <c>IGitService</c> after an edition with the agent platform provisioned it
/// (step 2f). Mirrors the daemon's <c>ProvisionRepo</c> response fields verbatim.
/// </summary>
public sealed record RepoSyncBinding(string RepoHandle, string SyncRemoteName, string SyncRemoteUrl);

/// <summary>
/// The agent-platform surface the shell talks to instead of naming <c>ControlCenterViewModel</c>
/// directly — so <c>MainWindowViewModel.ControlCenter</c> can be <c>null</c> under an edition
/// with no agent platform (1a). It exposes EXACTLY the members the shell references on the control
/// center, and — since step 2d — NO Pro-only concrete View/ViewModel type: the agent rail and the
/// resource monitor are reached only as opaque <c>object</c> and dropped into <c>ContentControl</c>s
/// that resolve their real View through <see cref="ViewLocator"/>. The remaining members are primitives
/// (the rail-section attention/spend adornments the shell binds through the window) plus C#-side hooks.
/// <see cref="ControlCenterViewModel"/> satisfies it; the agent-rail / resource-monitor concrete VMs are
/// reached only as <c>object</c> through it. Extends <see cref="IDisposable"/> because the shell disposes
/// its control center on teardown.
/// </summary>
public interface IAgentPlatformSurface : IDisposable
{
    // ---- rail-section adornments (bound in MainWindow.axaml through the window's ControlCenter) ----

    /// <summary>Coordinator attention badge visibility (the rail's amber dot).</summary>
    bool HasAttention { get; }

    /// <summary>Coordinator attention count (the rail badge number).</summary>
    int AttentionCount { get; }

    /// <summary>Today's token/USD spend, formatted for the Resources rail item.</summary>
    string SpendText { get; }

    // ---- Pro surfaces reached as opaque object (ViewLocator resolves the real View) ----

    /// <summary>The agent rail (worker list + kill switch) as opaque content — concretely an
    /// <c>AgentRailViewModel</c>, resolved to <c>AgentRailView</c> by <see cref="ViewLocator"/>. The shell
    /// holds it as <c>object?</c> (via <c>MainWindowViewModel.AgentRailContent</c>) and drops it into a
    /// <c>ContentControl</c>, so it never names the Pro rail types.</summary>
    object? AgentRailContent { get; }

    /// <summary>Build a task-manager resource monitor over the same backing services, returned as opaque
    /// content (concretely a <c>ResourceMonitorViewModel</c>, resolved to <c>ResourceMonitorView</c> by
    /// <see cref="ViewLocator"/>). The shell holds the result as <c>object?</c> and drops it into a
    /// <c>ContentControl</c>; the owner disposes it.</summary>
    object? CreateResourceMonitor();

    // ---- referenced from MainWindowViewModel (C#) ----

    /// <summary>Raised when the daemon first answers — the shell clears its degraded startup banner on it.</summary>
    event Action? DaemonReachable;

    /// <summary>Live (non-terminal) agent count the exit guard consults before a VM-stopping full exit.</summary>
    int LiveAgentCount { get; }

    /// <summary>Apply a coordinator-surface layout preset (File → Layout).</summary>
    void SetPreset(string preset);

    /// <summary>Propagate the direct-to-agent prompting mode to every open agent document.</summary>
    void SetDirectPrompting(bool allow);

    /// <summary>Make the coordinator conversation the surface's focus.</summary>
    void FocusCoordinator();

    /// <summary>Open (and focus) the given agent's document.</summary>
    void SelectAgent(string agentId);

    /// <summary>Point the live merge-queue projection at the daemon-provisioned repo handle.</summary>
    void SetActiveRepo(string repoHandle);

    /// <summary>
    /// Provision the just-opened repo into the daemon (P2-06) and return its sync-remote binding, or
    /// <c>null</c> when the daemon is unreachable / provisioning failed (agents are simply unavailable
    /// for the repo until the daemon is up — the Git client is unaffected). The Pro implementation owns
    /// the <c>DaemonClient</c> call; the reference-clean shell registers the returned remote with its own
    /// <c>IGitService</c> and calls <see cref="SetActiveRepo"/>, so it never names the daemon types (2f).
    /// </summary>
    Task<RepoSyncBinding?> ProvisionRepoAsync(string repoPath);
}
