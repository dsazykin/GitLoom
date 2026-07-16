using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
    private readonly IAgentService _agents;
    private readonly IMergeQueueService _queue;
    private readonly ICoordinatorService _coordinator;
    private readonly IKillSwitchService _kill;
    private readonly ITelemetryService _telemetry;
    private readonly IDisposable? _owner;
    private readonly Dictionary<string, AgentDocumentViewModel> _documents = new();

    // P2-13 #5/#6: the ONE reused per-agent dock workspace host (leak-free content-swap) + the live
    // terminal it hosts. The terminal + its daemon gateway/stream are rebuilt per agent and torn down here.
    private TerminalViewModel? _currentTerminal;
    private Services.ITerminalGateway? _currentTerminalGateway;
    private CancellationTokenSource? _terminalCts;

    public ObservableCollection<AgentRowViewModel> Agents { get; } = new();
    public QueueRailViewModel Queue { get; }
    public CoordinatorPanelViewModel Coordinator { get; }
    public TelemetryPanelViewModel Telemetry { get; }
    public VibeModeViewModel Vibe { get; }

    [ObservableProperty] private AgentDocumentViewModel? _selectedDocument;
    [ObservableProperty] private string? _selectedAgentId;

    /// <summary>P2-47 #7: the review cockpit overlay (non-null → shown), built from the live GetMergeDiff RPC.</summary>
    [ObservableProperty] private ReviewCockpitViewModel? _reviewCockpit;

    /// <summary>P2-13 #6: the per-agent dock workspace host (terminal + agent-diff + staging), reused across
    /// agent selections via <see cref="AgentWorkspaceViewModel.ShowAgent"/>. Null until an agent is opened.</summary>
    [ObservableProperty] private AgentWorkspaceViewModel? _workspace;

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

    // ---- Coordinator-as-CLI (PR3): the "Start coordinator" affordance ----

    /// <summary>The installed agent CLIs the coordinator picker offers (daemon-backed only).</summary>
    public ObservableCollection<Services.InstalledCliOption> InstalledClis { get; } = new();

    [ObservableProperty] private Services.InstalledCliOption? _selectedCli;
    [ObservableProperty] private bool _isStartingCoordinator;
    [ObservableProperty] private string _coordinatorStartError = "";

    /// <summary>A coordinator CLI session is live (its terminal is the way to talk to it).</summary>
    [ObservableProperty] private bool _isCoordinatorLive;

    /// <summary>True when the backing services can start CLI agents (a daemon, not the design mock)
    /// and no coordinator is live yet — gates the "Start coordinator" card.</summary>
    [ObservableProperty] private bool _canStartCoordinator;

    /// <summary>Default (design/harness) surface: runs on the scripted <see cref="MockOrchestrator"/>.
    /// The shipped app uses <see cref="ControlCenterViewModel(OrchestratorServices)"/> with a
    /// DaemonClient-backed bundle instead (P2-47).</summary>
    public ControlCenterViewModel() : this((MockOrchestrator?)null) { }

    /// <summary>Test seam: the headless harness injects a slow-tick mock for determinism.</summary>
    public ControlCenterViewModel(MockOrchestrator? mock)
        : this(OrchestratorServices.FromSingle(mock ?? new MockOrchestrator())) { }

    /// <summary>The real integration ctor (P2-47): the VM consumes only the seam interfaces, so the shipped
    /// app passes a DaemonClient-backed bundle and the design harness passes a mock — zero View changes.</summary>
    public ControlCenterViewModel(OrchestratorServices services)
    {
        _agents = services.Agents;
        _queue = services.Queue;
        _coordinator = services.Coordinator;
        _kill = services.Kill;
        _telemetry = services.Telemetry;
        _owner = services.Owner;

        Queue = new QueueRailViewModel(_queue, OpenReview);
        Coordinator = new CoordinatorPanelViewModel(_coordinator);
        Telemetry = new TelemetryPanelViewModel(_telemetry);
        // Vibe is headed for its own app (decision 2026-07-11); the VM stays alive here so
        // the render harness and the future shell keep a working surface, but nothing in
        // MainWindow routes to it.
        Vibe = new VibeModeViewModel(services.Vibe, _coordinator, () => { });

        _agents.EventReceived += OnAgentEvent;
        // Changed is raised by both the coordinator and the kill switch (same requery pattern).
        _coordinator.Changed += OnChanged;
        _kill.Changed += OnChanged;
        _telemetry.Sampled += OnSampled;
        ThemeManager.ThemeChanged += OnThemeChanged;

        RefreshAgents();
        RefreshKill();
        RefreshResources();
        RefreshCoordinatorCli();
        if (_agents is Services.ICliAgentHost)
        {
            _ = LoadInstalledClisAsync();
        }

        ApplyPreset(PersistedLayout(), persist: false); // restore File → Layout choice
        var first = Agents.FirstOrDefault();
        if (first is not null) SelectAgent(first.AgentId);
    }

    /// <summary>Live agents right now (the exit guard asks this before a VM-stopping full exit).</summary>
    public int LiveAgentCount => _agents.ListAgents()
        .Count(a => a.State is not (AgentLifecycleState.Merged or AgentLifecycleState.Rejected
            or AgentLifecycleState.Dead or AgentLifecycleState.TornDown));

    private static string PersistedLayout()
    {
        try { return App.Settings?.Current?.WorkspaceLayout ?? "FlightDeck"; }
        catch { return "FlightDeck"; }
    }

    // ---- event marshalling (events may arrive on the timer thread) ----

    private void OnAgentEvent(AgentEvent e) => Dispatcher.UIThread.Post(() =>
    {
        RefreshAgents();
        RefreshCoordinatorCli();
        Queue.Refresh();
        SelectedDocument?.Refresh();
        Vibe.OnOrchestratorEvent(e);
        if (e.Type is "attention_required" or "plan_pending") RefreshAttention();
    });

    private void OnChanged() => Dispatcher.UIThread.Post(() =>
    {
        Coordinator.Refresh();
        RefreshAgents();
        RefreshCoordinatorCli();
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
        AttentionCount = _coordinator.GetPendingPlans().Count
                       + _agents.ListAgents().Count(a => AttentionPolicy.IsAttentionRequired(a.State));
        HasAttention = AttentionCount > 0;
    }

    // Live theme switch: the badge converter resolves against the active theme variant, so nudge
    // each row to re-run it. (WeakReferenceMessenger-style discipline: unsubscribed on Dispose.)
    private void OnThemeChanged() => Dispatcher.UIThread.Post(() =>
    {
        foreach (var row in Agents) row.RefreshBadgeBrush();
    });

    /// <summary>Coordinator-CLI card state: live when any agent carries the coordinator role.</summary>
    private void RefreshCoordinatorCli()
    {
        var host = _agents as Services.ICliAgentHost;
        IsCoordinatorLive = host?.CoordinatorAgentId is { Length: > 0 }
            || _agents.ListAgents().Any(a => a.Role == GitLoom.Core.Agents.AgentRoles.Coordinator);
        CanStartCoordinator = host is not null && !IsCoordinatorLive;
    }

    /// <summary>Loads the installed-CLI picker (tolerates an unreachable daemon: honest empty + message).</summary>
    public async Task LoadInstalledClisAsync()
    {
        if (_agents is not Services.ICliAgentHost host)
        {
            return;
        }

        try
        {
            var clis = await host.ListInstalledClisAsync(CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                InstalledClis.Clear();
                foreach (var cli in clis) InstalledClis.Add(cli);
                SelectedCli ??= InstalledClis.FirstOrDefault();
                CoordinatorStartError = InstalledClis.Count == 0
                    ? "No agent CLIs are installed yet — add one in Settings → Agent CLIs."
                    : "";
            });
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.Unimplemented)
        {
            // The daemon answered — it just predates this RPC (version skew, e.g. an older
            // GitLoomOS payload). "Could not reach" would be a lie; name the real repair.
            await Dispatcher.UIThread.InvokeAsync(() =>
                CoordinatorStartError = "GitLoom's environment is older than this app and doesn't support "
                    + "starting a coordinator yet — update the GitLoom environment, then try again.");
        }
        catch (Exception)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                CoordinatorStartError = "GitLoom could not reach its agent daemon — it reconnects automatically; try again in a moment.");
        }
    }

    /// <summary>
    /// Start the coordinator: spawn the picked CLI (role <c>coordinator</c>) in its own jail and
    /// open its fully interactive terminal document — that terminal is how you talk to it (and
    /// where CLI login happens when no API key is stored).
    /// </summary>
    [RelayCommand]
    private async Task StartCoordinatorAsync()
    {
        if (_agents is not Services.ICliAgentHost host || SelectedCli is null || IsStartingCoordinator)
        {
            return;
        }

        IsStartingCoordinator = true;
        CoordinatorStartError = "";
        try
        {
            var agentId = await host.StartCoordinatorAsync(SelectedCli, CancellationToken.None);
            RefreshAgents();
            RefreshCoordinatorCli();
            SelectAgent(agentId); // opens the coordinator's interactive terminal document
        }
        catch (Exception ex)
        {
            CoordinatorStartError = ex.Message;
        }
        finally
        {
            IsStartingCoordinator = false;
        }
    }

    /// <summary>Re-open the live coordinator's terminal document from the coordinator surface.</summary>
    [RelayCommand]
    private void OpenCoordinatorTerminal()
    {
        var host = _agents as Services.ICliAgentHost;
        var agentId = host?.CoordinatorAgentId
            ?? _agents.ListAgents().FirstOrDefault(a => a.Role == GitLoom.Core.Agents.AgentRoles.Coordinator)?.AgentId;
        if (agentId is { Length: > 0 })
        {
            SelectAgent(agentId);
        }
    }

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

        // Mount the agent into the ONE reused dock workspace host (leak-free content-swap): a live terminal
        // as the primary pane, the agent document as the diff pane. Opening another agent costs three
        // content swaps, not a fresh dock graph.
        Workspace ??= new AgentWorkspaceViewModel(agentId, WorkspaceLayoutKind);
        var terminal = CreateTerminalFor(agentId);
        Workspace.ShowAgent(agentId, terminal, doc, null);

        IsCoordinatorFocus = false;
    }

    /// <summary>Builds (and attaches) a fresh live terminal for <paramref name="agentId"/>, tearing down the
    /// previous agent's terminal + its daemon gateway/stream first. Returns null on the mock/design harness
    /// (no PTY behind it → the pane shows its placeholder), and the attach tolerates a daemon that is down.</summary>
    private object? CreateTerminalFor(string agentId)
    {
        _currentTerminal?.Dispose();
        _currentTerminalGateway?.Dispose();
        _terminalCts?.Cancel();
        _terminalCts?.Dispose();
        _currentTerminal = null;
        _currentTerminalGateway = null;
        _terminalCts = null;

        if (_agents is not Services.DaemonBackedOrchestrator daemon)
        {
            return null;
        }

        var gateway = daemon.CreateTerminalGateway();
        var terminal = new TerminalViewModel(gateway);
        var cts = new CancellationTokenSource();
        _ = AttachTerminalAsync(terminal, agentId, cts.Token);

        _currentTerminal = terminal;
        _currentTerminalGateway = gateway;
        _terminalCts = cts;
        return terminal;
    }

    private static async Task AttachTerminalAsync(TerminalViewModel terminal, string agentId, CancellationToken ct)
    {
        try
        {
            await terminal.AttachAsync(agentId, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Daemon unreachable / PTY not yet bound / stream dropped — the pane stays empty (honest),
            // surfaced through the DaemonClient's ConnectionState rather than an app crash.
        }
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

    // The merge rail's "review" action opens the P2-11 cockpit built from the real branch-vs-main diff.
    private void OpenReview(string agentId) => _ = OpenReviewAsync(agentId);

    /// <summary>P2-47 #7: build the <see cref="ReviewCockpitContext"/> from the live GetMergeDiff RPC and
    /// mount the cockpit. On the mock/design harness — or when no repo/diff is available — it degrades to
    /// opening the agent's document, so nothing is fabricated and the surface never dead-ends.</summary>
    private async Task OpenReviewAsync(string agentId)
    {
        if (_agents is Services.DaemonBackedOrchestrator daemon)
        {
            Services.MergeDiffResult? diff = null;
            try { diff = await daemon.GetMergeDiffAsync(agentId, System.Threading.CancellationToken.None); }
            catch { diff = null; }

            if (diff is not null)
            {
                var name = Agents.FirstOrDefault(a => a.AgentId == agentId)?.Name ?? agentId;
                var ctx = new ReviewCockpitContext(agentId, name, diff.Branch, diff.Files);
                ReviewCockpit = new ReviewCockpitViewModel(ctx, onMerge: id => _ = _queue.ConfirmMergeAsync(id));
                return;
            }
        }

        // No live diff (mock harness, no active repo, or daemon down): fall back to the agent document.
        SelectAgent(agentId);
    }

    /// <summary>Dismiss the review cockpit overlay.</summary>
    [RelayCommand]
    public void CloseReview() => ReviewCockpit = null;

    /// <summary>P2-47 #1: point the live merge-queue projection at the daemon-provisioned repo handle so
    /// the merge rail + review cockpit reflect that repo's queue. No-op on the mock/design harness.</summary>
    public void SetActiveRepo(string repoHandle)
    {
        if (_agents is Services.DaemonBackedOrchestrator daemon)
        {
            daemon.SetActiveRepo(repoHandle);
        }
    }

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
        _agents.EventReceived -= OnAgentEvent;
        _coordinator.Changed -= OnChanged;
        _kill.Changed -= OnChanged;
        _telemetry.Sampled -= OnSampled;
        ThemeManager.ThemeChanged -= OnThemeChanged;

        // Tear down the live terminal + its gateway/stream, then the dock workspace host (closes any floating
        // dock windows — the documented Dock.Avalonia leak this host owns).
        _terminalCts?.Cancel();
        _currentTerminal?.Dispose();
        _currentTerminalGateway?.Dispose();
        _terminalCts?.Dispose();
        Workspace?.Dispose();

        _owner?.Dispose();
    }
}
