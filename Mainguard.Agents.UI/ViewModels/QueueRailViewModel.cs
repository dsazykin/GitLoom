using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The merge-queue rail (ControlCenterDesign.md §3): a projection of the P2-10 state machine —
/// its states, its stale cascade, its CanMerge gate — and nothing the machine can't do.
/// Refresh is event-driven requery (OPS §3.4: events refresh the projection; the gate re-reads).
/// </summary>
public partial class QueueRailViewModel : ViewModelBase
{
    private readonly IMergeQueueService _queue;
    private readonly Action<string> _openReview;

    public ObservableCollection<QueueEntryViewModel> Entries { get; } = new();

    [ObservableProperty] private string _mainShaText = "";
    [ObservableProperty] private string _gateText = "";
    [ObservableProperty] private bool _isEmpty;

    public QueueRailViewModel(IMergeQueueService queue, Action<string> openReview)
    {
        _queue = queue;
        _openReview = openReview;
        Refresh();
    }

    public void Refresh()
    {
        var snapshot = _queue.GetQueue();
        MainShaText = "main " + _queue.MainSha;

        // In-place sync so unchanged rows keep their visuals (no churn, no reflow).
        for (int i = Entries.Count - 1; i >= 0; i--)
            if (snapshot.All(q => q.AgentId != Entries[i].AgentId))
                Entries.RemoveAt(i);

        for (int i = 0; i < snapshot.Count; i++)
        {
            var entry = snapshot[i];
            var existing = Entries.FirstOrDefault(e => e.AgentId == entry.AgentId);
            if (existing is null)
            {
                existing = new QueueEntryViewModel(entry.AgentId, _openReview);
                Entries.Insert(Math.Min(i, Entries.Count), existing);
            }
            existing.Update(entry, _queue);
        }

        // Keep the rail in the service's order (Verified-fresh first).
        for (int i = 0; i < snapshot.Count; i++)
        {
            var target = Entries.FirstOrDefault(e => e.AgentId == snapshot[i].AgentId);
            var current = Entries.IndexOf(target!);
            if (current != i && current >= 0) Entries.Move(current, i);
        }

        IsEmpty = Entries.Count == 0;

        // The rail's ONE accent: the front-most fresh Verified entry gets the Review CTA.
        var first = Entries.FirstOrDefault(e => e.IsReviewable);
        foreach (var e in Entries) e.ShowReviewAccent = ReferenceEquals(e, first);

        // The gate line mirrors the front entry's CanMerge reason (§3.4).
        if (first is not null && !_queue.CanMerge(first.AgentId, out var reason))
            GateText = reason;
        else if (first is not null)
            GateText = "ready to merge";
        else
            GateText = "";
    }
}

/// <summary>One thread on the rail. State word first (E4/N-3); badge + brush are second channels.</summary>
public partial class QueueEntryViewModel : ViewModelBase
{
    private readonly Action<string> _openReview;

    public string AgentId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _branch = "";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _verifiedAgainst = "";
    [ObservableProperty] private string _badgeGeometryKey = "AgentWorkingIcon";
    [ObservableProperty] private bool _isReviewable;
    [ObservableProperty] private bool _showReviewAccent;

    [ObservableProperty] private bool _isNeutral;
    [ObservableProperty] private bool _isMutedState;
    [ObservableProperty] private bool _isInfoState;
    [ObservableProperty] private bool _isWarningState;
    [ObservableProperty] private bool _isSuccessState;
    [ObservableProperty] private bool _isDangerState;

    public QueueEntryViewModel(string agentId, Action<string> openReview)
    {
        AgentId = agentId;
        _openReview = openReview;
    }

    public void Update(QueueEntry entry, IMergeQueueService queue)
    {
        Name = entry.Name;
        Branch = entry.Branch;
        Detail = entry.Detail;
        StateWord = entry.State switch
        {
            WorkerMergeState.StaleVerified => "Stale",
            WorkerMergeState.AwaitingReview => "Awaiting review",
            var s => s.ToString(),
        };
        VerifiedAgainst = entry.Verification is { } v && entry.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview
            ? $"main@{v.MainSha}" : "";
        IsReviewable = entry.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview;

        (BadgeGeometryKey, IsNeutral, IsMutedState, IsInfoState, IsWarningState, IsSuccessState, IsDangerState) = entry.State switch
        {
            WorkerMergeState.Working => ("AgentWorkingIcon", true, false, false, false, false, false),
            WorkerMergeState.Verifying => ("AgentVerifyingIcon", false, false, true, false, false, false),
            WorkerMergeState.Verified => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.AwaitingReview => ("AgentWaitingIcon", false, false, false, false, true, false),
            WorkerMergeState.StaleVerified => ("AgentStaleIcon", false, false, false, true, false, false),
            WorkerMergeState.Merged => ("CheckmarkIcon", false, false, false, false, true, false),
            WorkerMergeState.Rejected => ("DismissIcon", false, false, false, false, false, true),
            _ => ("AgentWorkingIcon", false, true, false, false, false, false),
        };
    }

    [RelayCommand]
    private void OpenReview() => _openReview(AgentId);
}
