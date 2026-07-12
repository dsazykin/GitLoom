using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Agents;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The Coordinator conversation + two-phase TaskPlan approval (P2-14 / ControlCenterDesign.md §5).
/// Cards are decisions, lines are history; Approve is the panel's one accent.
/// </summary>
public partial class CoordinatorPanelViewModel : ViewModelBase
{
    private readonly ICoordinatorService _coordinator;

    public ObservableCollection<ChatLineViewModel> Transcript { get; } = new();

    [ObservableProperty] private PlanCardViewModel? _pendingPlan;
    [ObservableProperty] private string _composerText = "";
    [ObservableProperty] private string _pressureText = "";
    [ObservableProperty] private bool _isEmpty;

    public CoordinatorPanelViewModel(ICoordinatorService coordinator)
    {
        _coordinator = coordinator;
        Refresh();
    }

    public void Refresh()
    {
        var lines = _coordinator.GetTranscript();
        // Transcript is append-only in the mock; sync the tail.
        for (int i = Transcript.Count; i < lines.Count; i++)
            Transcript.Add(new ChatLineViewModel(lines[i]));
        IsEmpty = Transcript.Count == 0;

        var pending = _coordinator.GetPendingPlans();
        var first = pending.FirstOrDefault();
        if (first is null)
            PendingPlan = null;
        else if (PendingPlan?.PlanId != first.PlanId)
            PendingPlan = new PlanCardViewModel(first, DecideAsync);

        PressureText = pending.Count > 2
            ? $"{pending.Count} plans pending — the oldest has waited {(int)(DateTimeOffset.Now - pending.Min(p => p.DraftedAt)).TotalMinutes} min."
            : "";
    }

    private async Task DecideAsync(string planId, bool approve)
    {
        await _coordinator.SubmitPlanDecisionAsync(planId, approve);
        Refresh();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = ComposerText.Trim();
        if (text.Length == 0) return;
        ComposerText = "";
        await _coordinator.SendAsync(text);
        Refresh();
    }
}

/// <summary>One transcript line; Kind booleans let the View pick the rendering (no templates-by-type).</summary>
public sealed class ChatLineViewModel : ViewModelBase
{
    public string Text { get; }
    public string TimeText { get; }
    public bool IsHuman { get; }
    public bool IsCoordinator { get; }
    public bool IsToolCall { get; }
    public bool IsSystemLine { get; }
    public bool IsPlanCard { get; }
    public string SenderLabel { get; }

    public ChatLineViewModel(ChatLine line)
    {
        Text = line.Text;
        TimeText = line.At.ToLocalTime().ToString("HH:mm");
        IsHuman = line.Kind == ChatLineKind.Human;
        IsCoordinator = line.Kind == ChatLineKind.Coordinator;
        IsToolCall = line.Kind == ChatLineKind.ToolCall;
        IsSystemLine = line.Kind == ChatLineKind.SystemLine;
        IsPlanCard = line.Kind == ChatLineKind.PlanCard;
        SenderLabel = IsHuman ? "You" : IsCoordinator ? "Coordinator" : "";
    }
}

/// <summary>The TaskPlan approval card — Scope is the load-bearing field (§5.2).</summary>
public partial class PlanCardViewModel : ViewModelBase
{
    private readonly Func<string, bool, Task> _decide;

    public string PlanId { get; }
    public string Title { get; }
    public string ScopeText { get; }
    public string Approach { get; }
    public string TestStrategy { get; }
    public string FactsText { get; }

    [ObservableProperty] private bool _isDeciding;

    public PlanCardViewModel(TaskPlan plan, Func<string, bool, Task> decide)
    {
        _decide = decide;
        PlanId = plan.PlanId;
        Title = plan.Title;
        ScopeText = string.Join("\n", plan.Scope) + $"\n({plan.Scope.Count} files)";
        Approach = plan.Approach;
        TestStrategy = plan.TestStrategy;
        var age = (int)Math.Max(0, (DateTimeOffset.Now - plan.DraftedAt).TotalMinutes);
        FactsText = $"Budget ${plan.BudgetUsd:0.00} · drafted {age} min ago";
    }

    [RelayCommand] private Task ApproveAsync() { IsDeciding = true; return _decide(PlanId, true); }
    [RelayCommand] private Task RejectAsync() { IsDeciding = true; return _decide(PlanId, false); }
}
