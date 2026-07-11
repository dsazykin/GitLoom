using System;
using CommunityToolkit.Mvvm.ComponentModel;
using GitLoom.Core.Agents;

namespace GitLoom.App.ViewModels;

/// <summary>
/// One activity-bar agent row (P2-13 Row 1, LIFO). Exposes the lifecycle state as the badge
/// geometry key plus mutually-exclusive brush booleans so the View picks tokens — no color
/// in the VM (the house pattern). Badge forms per ControlCenterDesign.md §9.3.
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

    /// <summary>The collapsed rail's tooltip: name — state · current task (E4/TT-1).</summary>
    [ObservableProperty] private string _tooltip = "";

    // Brush booleans — the View maps each to a semantic token via style classes.
    [ObservableProperty] private bool _isNeutral;      // TextPrimary  (Working)
    [ObservableProperty] private bool _isMutedState;   // TextMuted    (Provisioning/Paused/Hibernated)
    [ObservableProperty] private bool _isInfoState;    // InfoBrush    (Verifying)
    [ObservableProperty] private bool _isWarningState; // WarningBrush (PlanPending/RateLimited/Unresponsive)
    [ObservableProperty] private bool _isSuccessState; // SuccessBrush (AwaitingReview/Merged)
    [ObservableProperty] private bool _isDangerState;  // DangerBrush  (Rejected/Dead)

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
        Tooltip = $"{Name} — {StateWord} · {info.Detail} · {Branch}";

        (BadgeGeometryKey, var brush) = info.State switch
        {
            AgentLifecycleState.Working        => ("AgentWorkingIcon", Brush.Neutral),
            AgentLifecycleState.Provisioning   => ("AgentProvisioningIcon", Brush.Muted),
            AgentLifecycleState.Yielding       => ("AgentVerifyingIcon", Brush.Muted),
            AgentLifecycleState.Paused         => ("AgentPausedIcon", Brush.Muted),
            AgentLifecycleState.ReviewHibernated => ("AgentPausedIcon", Brush.Muted),
            AgentLifecycleState.RateLimited    => ("AgentThrottledIcon", Brush.Warning),
            AgentLifecycleState.Unresponsive   => ("AgentUnresponsiveIcon", Brush.Warning),
            AgentLifecycleState.PlanPending    => ("AgentWaitingIcon", Brush.Warning),
            AgentLifecycleState.AwaitingReview => ("AgentWaitingIcon", Brush.Success),
            AgentLifecycleState.Merged         => ("CheckmarkIcon", Brush.Success),
            AgentLifecycleState.Rejected       => ("DismissIcon", Brush.Danger),
            AgentLifecycleState.Dead           => ("DismissIcon", Brush.Danger),
            _                                  => ("AgentWorkingIcon", Brush.Muted),
        };

        IsNeutral = brush == Brush.Neutral;
        IsMutedState = brush == Brush.Muted;
        IsInfoState = brush == Brush.Info;
        IsWarningState = brush == Brush.Warning;
        IsSuccessState = brush == Brush.Success;
        IsDangerState = brush == Brush.Danger;
    }

    private enum Brush { Neutral, Muted, Info, Warning, Success, Danger }
}
