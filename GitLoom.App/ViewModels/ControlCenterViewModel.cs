using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels.Agents;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Mock;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The Phase-2 Control Center prototype shell (Lane E Part 3; docs/design/ControlCenterDesign.md).
/// Runs entirely on <see cref="MockOrchestrator"/> — the ViewModels consume only the service
/// interfaces, so the mock can later be swapped for a DaemonClient with zero View changes.
/// Refresh model: OPS §3.4 — events refresh the projection; every gate re-reads state.
/// </summary>
public partial class ControlCenterViewModel : ViewModelBase, IDisposable
{
    private readonly MockOrchestrator _mock;
    private readonly IAgentService _agents;
    private readonly IMergeQueueService _queue;
    private readonly IKillSwitchService _kill;
    private readonly ITelemetryService _telemetry;
    private readonly Dictionary<string, AgentDocumentViewModel> _documents = new();

    public ObservableCollection<AgentRowViewModel> Agents { get; } = new();
    public QueueRailViewModel Queue { get; }
    public CoordinatorPanelViewModel Coordinator { get; }
    public TelemetryPanelViewModel Telemetry { get; }
    public VibeModeViewModel Vibe { get; }

    [ObservableProperty] private AgentDocumentViewModel? _selectedDocument;
    [ObservableProperty] private string? _selectedAgentId;

    // Kill switch (P2-14): quiet at rest, instant, recoverable — see §5.4 for why no confirm.
    [ObservableProperty] private bool _isFrozen;
    [ObservableProperty] private string _freezeBannerText = "";
    [ObservableProperty] private string _killSwitchLabel = "Stop all";

    // Activity bar row 0 (P2-13): resource sparkline + token spend.
    [ObservableProperty] private Points _cpuPoints = new();
    [ObservableProperty] private string _spendText = "$0.00";
    [ObservableProperty] private int _attentionCount;
    [ObservableProperty] private bool _hasAttention;

    // Workspace layouts (revised 2026-07-11: two presets, The Loom removed; applies to
    // the coordinator surfaces only — the Repo viewer is untouched). Persisted like Theme.
    [ObservableProperty] private bool _isFlightDeck = true;
    [ObservableProperty] private bool _isConversationDeck;

    /// <summary>True: the coordinator conversation is the surface's center content;
    /// false: the selected agent's document is. Driven by the section rail.</summary>
    [ObservableProperty] private bool _isCoordinatorFocus = true;

    public ControlCenterViewModel() : this(null) { }

    /// <summary>Test seam: the headless harness injects a slow-tick mock for determinism.</summary>
    public ControlCenterViewModel(MockOrchestrator? mock)
    {
        _mock = mock ?? new MockOrchestrator();
        _agents = _mock; _queue = _mock; _kill = _mock; _telemetry = _mock;

        Queue = new QueueRailViewModel(_queue, OpenReview);
        Coordinator = new CoordinatorPanelViewModel(_mock);
        Telemetry = new TelemetryPanelViewModel(_telemetry);
        // Vibe is headed for its own app (decision 2026-07-11); the VM stays alive here so
        // the render harness and the future shell keep a working surface, but nothing in
        // MainWindow routes to it.
        Vibe = new VibeModeViewModel(_mock, _mock, () => { });

        _mock.EventReceived += OnAgentEvent;
        _mock.Changed += OnChanged;
        _mock.Sampled += OnSampled;
        ThemeManager.ThemeChanged += OnThemeChanged;

        RefreshAgents();
        RefreshKill();
        RefreshResources();
        ApplyPreset(PersistedLayout(), persist: false); // restore File → Layout choice
        var first = Agents.FirstOrDefault();
        if (first is not null) SelectAgent(first.AgentId);
    }

    private static string PersistedLayout()
    {
        try { return App.Settings?.Current?.WorkspaceLayout ?? "FlightDeck"; }
        catch { return "FlightDeck"; }
    }

    // ---- event marshalling (events may arrive on the timer thread) ----

    private void OnAgentEvent(AgentEvent e) => Dispatcher.UIThread.Post(() =>
    {
        RefreshAgents();
        Queue.Refresh();
        SelectedDocument?.Refresh();
        Vibe.OnOrchestratorEvent(e);
        if (e.Type is "attention_required" or "plan_pending") RefreshAttention();
    });

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        Coordinator.Refresh();
        RefreshAgents();
        Queue.Refresh();
        RefreshKill();
        RefreshAttention();
    });

    private void OnSampled() => Dispatcher.UIThread.Post(() =>
    {
        RefreshResources();
        Telemetry.Refresh();
        SelectedDocument?.Refresh();
        Vibe.OnTick();
    });

    // ---- projections ----

    private void RefreshAgents()
    {
        var snapshot = _agents.ListAgents();
        for (int i = Agents.Count - 1; i >= 0; i--)
            if (snapshot.All(a => a.AgentId != Agents[i].AgentId))
                Agents.RemoveAt(i);
        foreach (var info in snapshot.OrderByDescending(a => a.SpawnedAt)) // LIFO (P2-13)
        {
            var existing = Agents.FirstOrDefault(r => r.AgentId == info.AgentId);
            if (existing is null) Agents.Insert(0, new AgentRowViewModel(info));
            else existing.Update(info);
        }
        RefreshAttention();
    }

    private void RefreshAttention()
    {
        // The attention badge is a static count, never a pulse (§2.4 rationale). Derivation lives
        // in the pure AttentionPolicy so the rail count and the per-row flag agree.
        AttentionCount = _mock.GetPendingPlans().Count
                       + _agents.ListAgents().Count(a => AttentionPolicy.IsAttentionRequired(a.State));
        HasAttention = AttentionCount > 0;
    }

    // Live theme switch: the badge converter resolves against the active theme variant, so nudge
    // each row to re-run it. (WeakReferenceMessenger-style discipline: unsubscribed on Dispose.)
    private void OnThemeChanged() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var row in Agents) row.RefreshBadgeBrush();
    });

    private void RefreshKill()
    {
        IsFrozen = _kill.IsFrozen;
        FreezeBannerText = _kill.PhaseText;
        KillSwitchLabel = IsFrozen ? "Frozen — resume" : "Stop all";
    }

    private void RefreshResources()
    {
        var history = _telemetry.History;
        var points = new Points();
        int n = Math.Min(60, history.Count);
        for (int i = 0; i < n; i++)
        {
            var s = history[history.Count - n + i];
            points.Add(new Point(i * (60.0 / Math.Max(1, n - 1)), 16 - s.CpuPercent / 100.0 * 16));
        }
        CpuPoints = points;
        SpendText = FormattableString.Invariant($"${_telemetry.Current.SpendTodayUsd:0.00}");
    }

    // ---- selection / navigation ----

    [RelayCommand]
    public void SelectAgent(string agentId)
    {
        SelectedAgentId = agentId;
        if (!_documents.TryGetValue(agentId, out var doc))
        {
            doc = new AgentDocumentViewModel(agentId, _agents, _queue, _telemetry);
            doc.SetDirectPrompting(AllowDirectPrompting);
            _documents[agentId] = doc;
        }
        doc.Refresh();
        SelectedDocument = doc;
        IsCoordinatorFocus = false;
    }

    [RelayCommand]
    public void FocusCoordinator() => IsCoordinatorFocus = true;

    /// <summary>Wires a task-manager resource monitor onto the same mock daemon; the
    /// owner disposes the returned VM.</summary>
    public ResourceMonitorViewModel CreateResourceMonitor() => new(_agents, _telemetry);

    /// <summary>Direct-to-agent prompting toggle (File menu → Agent prompting): propagates
    /// to every open agent document; new documents inherit it.</summary>
    public bool AllowDirectPrompting { get; private set; } = true;

    public void SetDirectPrompting(bool allow)
    {
        AllowDirectPrompting = allow;
        foreach (var doc in _documents.Values) doc.SetDirectPrompting(allow);
    }

    private void OpenReview(string agentId) => SelectAgent(agentId);

    // ---- kill switch ----

    [RelayCommand]
    private async Task ToggleKillSwitchAsync()
    {
        if (_kill.IsFrozen) await _kill.ResumeAsync();
        else await _kill.EngageAsync(); // no confirm: instant, recoverable by design (§5.4)
        RefreshKill();
        RefreshAgents();
        Queue.Refresh();
    }

    // ---- presets & mode ----

    [RelayCommand]
    public void SetPreset(string preset) => ApplyPreset(preset, persist: true);

    private void ApplyPreset(string preset, bool persist)
    {
        // Unknown/legacy values (e.g. the retired "Loom") fall back to Flight Deck.
        IsConversationDeck = preset == "ConversationDeck";
        IsFlightDeck = !IsConversationDeck;
        if (persist)
        {
            try { App.Settings?.Update(p => p.WorkspaceLayout = IsConversationDeck ? "ConversationDeck" : "FlightDeck"); }
            catch { /* settings unavailable (headless) — in-memory only */ }
        }
    }

    /// <summary>The persisted layout as the workspace-dock enum, so a dock workspace opens in the
    /// same arrangement the coordinator surface uses.</summary>
    public WorkspaceLayoutKind WorkspaceLayoutKind =>
        IsConversationDeck ? WorkspaceLayoutKind.ConversationDeck : WorkspaceLayoutKind.FlightDeck;

    public void Dispose()
    {
        _mock.EventReceived -= OnAgentEvent;
        _mock.Changed -= OnChanged;
        _mock.Sampled -= OnSampled;
        ThemeManager.ThemeChanged -= OnThemeChanged;
        _mock.Dispose();
    }
}
