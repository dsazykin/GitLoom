using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// One activity-bar agent row (P2-13 Row 1, LIFO). Exposes the lifecycle state as the badge
/// geometry key plus a single <see cref="AgentStatus"/> — the View colours the micro-badge through
/// the one <c>AgentStatusBrushConverter</c>, so there is no color and no second status→brush map in
/// the VM (P2-13 invariant #2). Badge forms per ControlCenterDesign.md §9.3.
/// </summary>
public partial class AgentRowViewModel : ViewModelBase
{
    public string AgentId { get; }
    public string Name { get; }
    public string Branch { get; }

    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _badgeGeometryKey = "AgentWorkingIcon";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private double _rowOpacity = 1.0;

    /// <summary>The badge status — the single input to the one <c>AgentStatusBrushConverter</c>.
    /// The View binds the micro-badge Foreground to this; no color lives in the VM.</summary>
    [ObservableProperty] private AgentStatus _status = AgentStatus.Working;

    /// <summary>True while this agent needs the human (drives the row's attention affordance).</summary>
    [ObservableProperty] private bool _needsAttention;

    /// <summary>The collapsed rail's tooltip: name — state · current task (E4/TT-1).</summary>
    [ObservableProperty] private string _tooltip = "";

    /// <summary>The row's role label: "coordinator" for the coordinator CLI, "subagent" for a
    /// coordinator-spawned managed worker, empty for a manual agent. Text, not color — the badge
    /// stays the one status channel.</summary>
    [ObservableProperty] private string _roleLabel = "";

    [ObservableProperty] private bool _hasRoleLabel;

    public AgentRowViewModel(AgentInfo info)
    {
        AgentId = info.AgentId;
        Name = info.Name;
        Branch = info.Branch;
        Update(info);
    }

    public void Update(AgentInfo info)
    {
        Detail = info.Detail;
        StateWord = info.State.ToString();
        RowOpacity = info.State == AgentLifecycleState.ReviewHibernated ? 0.60 : 1.0;
        RoleLabel = info.Role switch
        {
            AgentRoles.Coordinator => "coordinator",
            AgentRoles.Managed => "subagent",
            _ => "",
        };
        HasRoleLabel = RoleLabel.Length > 0;
        Tooltip = HasRoleLabel
            ? $"{Name} ({RoleLabel}) — {StateWord} · {info.Detail} · {Branch}"
            : $"{Name} — {StateWord} · {info.Detail} · {Branch}";
        Status = AgentStatusMap.FromLifecycle(info.State);
        NeedsAttention = AttentionPolicy.IsAttentionRequired(Status);

        // The glyph carries the state (shape is the primary channel, E1/E2); colour is the second
        // channel and comes from the converter via Status.
        BadgeGeometryKey = info.State switch
        {
            AgentLifecycleState.Working => "AgentWorkingIcon",
            AgentLifecycleState.Provisioning => "AgentProvisioningIcon",
            AgentLifecycleState.Yielding => "AgentVerifyingIcon",
            AgentLifecycleState.Paused => "AgentPausedIcon",
            AgentLifecycleState.ReviewHibernated => "AgentPausedIcon",
            AgentLifecycleState.RateLimited => "AgentThrottledIcon",
            AgentLifecycleState.Unresponsive => "AgentUnresponsiveIcon",
            AgentLifecycleState.PlanPending => "AgentWaitingIcon",
            AgentLifecycleState.AwaitingReview => "AgentWaitingIcon",
            AgentLifecycleState.Merged => "CheckmarkIcon",
            AgentLifecycleState.Rejected => "DismissIcon",
            AgentLifecycleState.Dead => "DismissIcon",
            _ => "AgentWorkingIcon",
        };
    }

    /// <summary>Re-raise <see cref="Status"/> so the badge binding re-runs the converter after a
    /// live theme switch (the converter resolves against the active theme variant).</summary>
    public void RefreshBadgeBrush() => OnPropertyChanged(nameof(Status));
}
