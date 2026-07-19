using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Git.Review;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The pinned flagged-changes gate panel (ControlCenterDesign §6.3). Renders every must-acknowledge item
/// for the branch — flag-worthy risk hunks, F6 out-of-approved-scope files, lockfile CVE/script rows (all
/// via the branch's <see cref="AcknowledgmentStore"/>) plus the RT-D2 changed-test-command item (via the
/// P2-10 <see cref="ChangedTestCommandGate"/>). Each item is acknowledged <b>individually</b>; there is no
/// global checkbox (rejection trigger). Acks are hash-bound: a new push resets them and the panel says so.
/// This View-model only renders + forwards acks — all rule logic lives in pure Core (invariant 1).
/// </summary>
public partial class FlaggedChangesPanelViewModel : ViewModelBase
{
    private readonly AcknowledgmentStore _store;
    private readonly ChangedTestCommandGate? _changedGate;
    private readonly string _agentId;
    private readonly bool _changedTestCommand;
    private readonly Action? _onChanged;

    public ObservableCollection<FlaggedItemRowViewModel> Items { get; } = new();

    [ObservableProperty] private bool _hasItems;
    [ObservableProperty] private int _pendingCount;
    [ObservableProperty] private bool _allAcknowledged;
    [ObservableProperty] private string _resetNotice = "";

    /// <param name="store">The branch's per-item acknowledgment ledger (already loaded with its flagged set).</param>
    /// <param name="agentId">The branch/agent id (for the RT-D2 gate ack).</param>
    /// <param name="changedGate">The P2-10 changed-test-command gate (RT-D2), or null when not wired.</param>
    /// <param name="changedTestCommand">True when the branch's resolved test command drifted from main (RT-D2).</param>
    /// <param name="onChanged">Invoked after any ack so the cockpit can re-read <c>CanMerge</c>.</param>
    public FlaggedChangesPanelViewModel(
        AcknowledgmentStore store,
        string agentId,
        ChangedTestCommandGate? changedGate = null,
        bool changedTestCommand = false,
        Action? onChanged = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _agentId = agentId ?? "";
        _changedGate = changedGate;
        _changedTestCommand = changedTestCommand;
        _onChanged = onChanged;
        Refresh();
    }

    public void Refresh()
    {
        Items.Clear();

        foreach (var item in _store.Items)
        {
            Items.Add(new FlaggedItemRowViewModel(
                item.Id,
                item.Path,
                item.Category.ToString(),
                item.Detail,
                item.Kind,
                _store.IsAcknowledged(item.Id),
                AcknowledgeStoreItem));
        }

        // The RT-D2 changed-test-command item lives on its own gate (P2-10 owns it), rendered here.
        if (_changedTestCommand && _changedGate is not null)
        {
            var acked = !_changedGate.IsUnacknowledged(_agentId);
            Items.Add(new FlaggedItemRowViewModel(
                "changed-test-command",
                "(verification command)",
                RiskCategory.ExecutableConfig.ToString(),
                "the test command changed on this branch vs main — a branch cannot self-green",
                FlaggedKind.ChangedTestCommand,
                acked,
                AcknowledgeChangedTestCommand));
        }

        var pending = Items.Count(i => !i.IsAcknowledged);
        PendingCount = pending;
        HasItems = Items.Count > 0;
        AllAcknowledged = pending == 0;
        ResetNotice = _store.LastResetCount > 0
            ? $"The branch changed since you acknowledged — {_store.LastResetCount} item(s) reset."
            : "";
    }

    private void AcknowledgeStoreItem(string itemId)
    {
        _store.Acknowledge(itemId);
        Refresh();
        _onChanged?.Invoke();
    }

    private void AcknowledgeChangedTestCommand(string _)
    {
        _changedGate?.Acknowledge(_agentId);
        Refresh();
        _onChanged?.Invoke();
    }
}

/// <summary>One flagged item row: severity-first (§9.3 octagon = must-acknowledge), the fact, its own ack.</summary>
public partial class FlaggedItemRowViewModel : ViewModelBase
{
    private readonly Action<string> _onAcknowledge;

    public string ItemId { get; }
    public string Path { get; }
    public string CategoryWord { get; }
    public string Detail { get; }
    public FlaggedKind Kind { get; }

    /// <summary>All flagged items are must-acknowledge → the octagon severity glyph (§9.3 E-family).</summary>
    public string SeverityGlyphKey => "SeverityBlockerIcon";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AckLabel))]
    private bool _isAcknowledged;

    /// <summary>The ack button label (E4 — a state word, not a bare checkbox).</summary>
    public string AckLabel => IsAcknowledged ? "Acknowledged" : "Acknowledge";

    public FlaggedItemRowViewModel(
        string itemId,
        string path,
        string categoryWord,
        string detail,
        FlaggedKind kind,
        bool isAcknowledged,
        Action<string> onAcknowledge)
    {
        ItemId = itemId;
        Path = path;
        CategoryWord = categoryWord;
        Detail = detail;
        Kind = kind;
        _isAcknowledged = isAcknowledged;
        _onAcknowledge = onAcknowledge;
    }

    [RelayCommand]
    private void Acknowledge()
    {
        if (!IsAcknowledged)
        {
            _onAcknowledge(ItemId);
        }
    }
}
