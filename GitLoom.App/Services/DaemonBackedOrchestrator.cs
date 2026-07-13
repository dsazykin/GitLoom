using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using Proto = GitLoom.Protos.V1;

namespace GitLoom.App.Services;

/// <summary>
/// P2-47 — the real, DaemonClient-backed implementation of the control-center seams, replacing
/// <see cref="Core.Agents.Mock.MockOrchestrator"/> in the shipped app. It runs the P2-02
/// <c>StreamAgentEvents</c> snapshot-then-deltas stream in the background, keeps a live agent projection,
/// and serves <see cref="IAgentService.ListAgents"/> from it — so the Activity Bar shows the daemon's real
/// agents, not scripted ones. <see cref="EndAgentAsync"/> routes to the real <c>StopAgent</c> RPC.
///
/// <para><b>Honest degradation (P2-47 residuals — NOT mocks).</b> The interfaces model surfaces whose gRPC
/// contracts do not exist yet (the merge-queue detail projection, the coordinator conversation, the kill
/// switch, per-agent telemetry, Vibe). Those members return empty/neutral state and their steering calls
/// are no-ops here; each is marked below. They light up as their RPCs are added — this adapter is the seam
/// that lets that happen without touching a View. It is deliberately NOT a mock: it invents no agents,
/// no plans, no samples.</para>
/// </summary>
public sealed class DaemonBackedOrchestrator :
    IAgentService, IMergeQueueService, ICoordinatorService,
    IKillSwitchService, ITelemetryService, IVibeService, IDisposable
{
    private readonly DaemonClient _client;
    private readonly bool _ownsClient;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentInfo> _agents = new(StringComparer.Ordinal);
    private Task? _pump;
    private long _seq;

    public DaemonBackedOrchestrator(DaemonClient client, bool ownsClient = true)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
    }

    /// <summary>The shipped-app bundle: a loopback DaemonClient behind every control-center seam.</summary>
    public static OrchestratorServices CreateBundle()
    {
        var adapter = new DaemonBackedOrchestrator(DaemonClient.ForLoopback());
        adapter.Start();
        return OrchestratorServices.FromSingle(adapter);
    }

    /// <summary>Starts the background event-stream pump (idempotent). Construction never blocks on the
    /// daemon; the pump tolerates an unreachable daemon (DaemonClient reconnects / stays Down).</summary>
    public void Start()
    {
        if (_pump is not null)
        {
            return;
        }

        _pump = Task.Run(() => PumpAsync(_cts.Token));
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _client.StreamAgentEventsAsync(ct).ConfigureAwait(false))
            {
                ApplyEvent(e);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception)
        {
            // The stream loop already backs off internally; a terminal fault just leaves the last
            // projection in place. Never surface to the UI thread as an unhandled exception.
        }
    }

    private void ApplyEvent(Proto.AgentEvent e)
    {
        switch (e.EventCase)
        {
            case Proto.AgentEvent.EventOneofCase.Snapshot:
                lock (_gate)
                {
                    _agents.Clear();
                    foreach (var a in e.Snapshot.Agents)
                    {
                        _agents[a.AgentId] = MapInfo(a);
                    }
                }
                break;

            case Proto.AgentEvent.EventOneofCase.State:
                lock (_gate)
                {
                    if (_agents.TryGetValue(e.AgentId, out var existing))
                    {
                        _agents[e.AgentId] = existing with { State = MapState(e.State.State) };
                    }
                    else
                    {
                        _agents[e.AgentId] = new AgentInfo(
                            e.AgentId, e.AgentId, $"agent/{e.AgentId}",
                            MapState(e.State.State), string.Empty, DateTimeOffset.UtcNow);
                    }
                }
                break;

            case Proto.AgentEvent.EventOneofCase.Log:
                // Log lines feed the terminal tail once that surface is wired (residual); ignored here.
                break;
        }

        EventReceived?.Invoke(new AgentEvent(
            Interlocked.Increment(ref _seq), e.EventCase.ToString(), e.AgentId, string.Empty, DateTimeOffset.UtcNow));
        Changed?.Invoke();
    }

    private static AgentInfo MapInfo(Proto.AgentInfo a) =>
        new(a.AgentId, string.IsNullOrEmpty(a.AgentKind) ? a.AgentId : a.AgentKind,
            $"agent/{a.AgentId}", MapState(a.State), string.Empty, DateTimeOffset.UtcNow);

    // The daemon carries a free-form state string (G-14). Parse it into the UI lifecycle enum
    // case-insensitively; an unrecognized word (e.g. the daemon's "Starting") maps to Working so a live
    // agent still renders rather than vanishing.
    private static AgentLifecycleState MapState(string? state) =>
        Enum.TryParse<AgentLifecycleState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : AgentLifecycleState.Working;

    // ---- IAgentService (LIVE: list + event stream + stop) ----

    public IReadOnlyList<AgentInfo> ListAgents()
    {
        lock (_gate)
        {
            return _agents.Values.OrderByDescending(a => a.SpawnedAt).ToArray();
        }
    }

    public event Action<AgentEvent>? EventReceived;

    /// <summary>End task → the real <c>StopAgent</c> RPC.</summary>
    public async Task EndAgentAsync(string agentId)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try { await _client.StopAgentAsync(agentId, cts.Token).ConfigureAwait(false); }
        catch (Exception) { /* daemon unreachable — surfaced via ConnectionState, not an app crash. */ }
    }

    // Residuals: no per-agent pause/prompt/plan-tree RPCs yet (P2-47). No-ops / empty, never fabricated.
    public Task PauseAgentAsync(string agentId) => Task.CompletedTask;
    public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;
    public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;
    public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();
    public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;
    public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();
    public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) =>
        Array.Empty<(string, bool)>();

    // ---- IMergeQueueService (residual: no merge-queue projection RPC yet) ----

    public string MainSha => string.Empty;
    public IReadOnlyList<QueueEntry> GetQueue() => Array.Empty<QueueEntry>();
    public bool CanMerge(string agentId, out string reason)
    {
        reason = "The merge-queue projection RPC is not wired yet (P2-47 residual).";
        return false;
    }
    public Task ConfirmMergeAsync(string agentId) => Task.CompletedTask;
    public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

    // ---- ICoordinatorService (residual: no coordinator conversation RPC yet) ----

    public IReadOnlyList<ChatLine> GetTranscript() => Array.Empty<ChatLine>();
    public IReadOnlyList<TaskPlan> GetPendingPlans() => Array.Empty<TaskPlan>();
    public TaskPlan? GetPlan(string planId) => null;
    public event Action? Changed;
    public Task SendAsync(string text) => Task.CompletedTask;
    public Task SubmitPlanDecisionAsync(string planId, bool approve) => Task.CompletedTask;

    // ---- IKillSwitchService (residual: no kill-switch RPC wrapper on DaemonClient yet) ----

    public bool IsFrozen => false;
    public KillSwitchPhase Phase => KillSwitchPhase.Armed;
    public string PhaseText => string.Empty;
    // ICoordinatorService.Changed doubles as the kill-switch Changed (same requery pattern); no second event.
    event Action? IKillSwitchService.Changed { add { Changed += value; } remove { Changed -= value; } }
    public Task EngageAsync() => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;

    // ---- ITelemetryService (residual: no telemetry stream RPC on DaemonClient yet) ----

    public IReadOnlyList<SandboxEvent> GetSandboxEvents(string? agentId = null) => Array.Empty<SandboxEvent>();
    public ResourceSample Current => new(DateTimeOffset.UtcNow, 0, 0, 0m);
    public IReadOnlyList<ResourceSample> History => Array.Empty<ResourceSample>();
    public IReadOnlyList<AgentResourceUsage> GetAgentUsage() => Array.Empty<AgentResourceUsage>();
    public event Action? Sampled { add { } remove { } }

    // ---- IVibeService (Vibe is a separate future app — intentionally inert here) ----

    public IReadOnlyList<Checkpoint> GetCheckpoints() => Array.Empty<Checkpoint>();
    public Checkpoint? LastVerifiedGreen => null;
    public Task RestoreCheckpointAsync(string sha) => Task.CompletedTask;
    public DeployStatus Deploy => new(DeployPhase.Idle, null, null);
    public event Action? DeployChanged { add { } remove { } }
    public Task PublishAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _cts.Cancel();
        try { _pump?.Wait(TimeSpan.FromSeconds(2)); } catch { /* pump cancellation */ }
        _cts.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
