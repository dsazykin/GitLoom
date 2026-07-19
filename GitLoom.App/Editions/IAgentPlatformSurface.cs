using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Editions;

/// <summary>
/// The agent-platform surface the shell talks to instead of naming <c>ControlCenterViewModel</c>
/// directly — so <see cref="MainWindowViewModel.ControlCenter"/> can be <c>null</c> under an edition
/// with no agent platform (1a). It exposes EXACTLY the members the shell references on the control
/// center: the compiled XAML bindings in <c>MainWindow.axaml</c> resolve against this interface, so a
/// missing member is a XAML compile error. <see cref="ControlCenterViewModel"/> satisfies it with zero
/// behavioral change (it already declares every member with these exact signatures). Extends
/// <see cref="IDisposable"/> because the shell disposes its control center on teardown.
/// </summary>
public interface IAgentPlatformSurface : IDisposable
{
    // ---- bound in MainWindow.axaml (compiled bindings on x:DataType="vm:MainWindowViewModel") ----

    /// <summary>Coordinator attention badge visibility (the rail's amber dot).</summary>
    bool HasAttention { get; }

    /// <summary>Coordinator attention count (the rail badge number).</summary>
    int AttentionCount { get; }

    /// <summary>Today's token/USD spend, formatted for the Resources rail item.</summary>
    string SpendText { get; }

    /// <summary>The live worker agents shown in the rail's agent list (its item template binds
    /// <c>x:DataType="vm:AgentRowViewModel"</c>, so the element type must stay exact).</summary>
    ObservableCollection<AgentRowViewModel> Agents { get; }

    /// <summary>Kill-switch frozen state (drives the rail button's <c>frozen</c> class).</summary>
    bool IsFrozen { get; }

    /// <summary>The kill-switch toggle command (its exact command type must survive so the rail binds).</summary>
    IAsyncRelayCommand ToggleKillSwitchCommand { get; }

    /// <summary>The kill-switch button label ("Stop all" / "Frozen — resume").</summary>
    string KillSwitchLabel { get; }

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

    /// <summary>Build a task-manager resource monitor over the same backing services (owner disposes it).</summary>
    ResourceMonitorViewModel CreateResourceMonitor();
}
