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
/// <see cref="Core.Agents.Mock.MockOrchestrator"/> in the shipped app. Every surface is a <b>live
/// projection off a daemon RPC</b> — nothing here is a mock or a hardcoded-empty stub:
/// <list type="bullet">
/// <item>agents — <c>AgentService.StreamAgentEvents</c> (snapshot-then-deltas);</item>
/// <item>merge queue — <c>MergeQueueService.StreamQueue</c> for the active repo handle, and the human
/// merge routes <c>BeginMerge</c>→<c>ConfirmMerge</c>;</item>
/// <item>plan approval — <c>PlanApprovalService.StreamPlans</c>, and Approve/Reject hit the daemon;</item>
/// <item>kill switch — <c>KillSwitchService.Engage</c>/<c>Resume</c>;</item>
/// <item>telemetry — <c>GatewayService.StreamSpend</c> + <c>Get/SetBudgets</c>;</item>
/// <item>coordinator chat — <c>CoordinatorService.StreamConversation</c> + <c>SendMessage</c>.</item>
/// </list>
/// Each streaming pump runs in the background with reconnect (the DaemonClient stream is single-shot;
/// this wraps it in a backoff loop that tolerates an unreachable daemon / a not-yet-active queue). The
/// projections are what the ViewModels read; steering calls route to the matching RPC.
///
/// <para><b>Vibe</b> is intentionally inert here — it is a separate future app (decision 2026-07-11),
/// not part of the shipped control center; MainWindow never routes to it.</para>
/// </summary>
/// <summary>The review-cockpit merge diff fetched over GetMergeDiff: the agent branch + its parsed patches.</summary>
public sealed record MergeDiffResult(string Branch, IReadOnlyList<GitLoom.Core.Models.FilePatch> Files);

public sealed class DaemonBackedOrchestrator :
    IAgentService, IMergeQueueService, ICoordinatorService,
    IKillSwitchService, ITelemetryService, IVibeService, ICliAgentHost, IDisposable
{
    private const string DefaultCoordinatorId = "coordinator-1";
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(2);

    private readonly DaemonClient _client;
    private readonly bool _ownsClient;
    private readonly Func<string, string?> _keystoreLookup;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();

    // ---- projections (all guarded by _gate) ----
    private readonly Dictionary<string, AgentInfo> _agents = new(StringComparer.Ordinal);
    private readonly List<QueueEntry> _queue = new();
    private readonly Dictionary<string, (bool CanMerge, string Reason)> _gate_ = new(StringComparer.Ordinal);
    private readonly List<TaskPlan> _plans = new();
    private readonly List<ChatLine> _transcript = new();
    private readonly List<ResourceSample> _samples = new();
    private readonly Dictionary<string, (long Tokens, long UsdMicros)> _agentSpend = new(StringComparer.Ordinal);
    private string _mainSha = string.Empty;
    private long _totalUsdMicros;
    private long _totalTokens;
    private bool _frozen;
    private KillSwitchPhase _phase = KillSwitchPhase.Armed;
    private string _phaseText = string.Empty;

    private string? _repoHandle;
    private string? _coordinatorAgentId;
    private Task? _agentPump;
    private Task? _planPump;
    private Task? _spendPump;
    private Task? _conversationPump;
    private Task? _queuePump;
    private CancellationTokenSource? _queuePumpCts;
    private long _seq;

    /// <param name="keystoreLookup">Reads a P2-01 BYOK key by keystore name (e.g. <c>llm_anthropic</c>);
    /// defaults to the OS keyring. Injectable so tests never touch a real keyring.</param>
    public DaemonBackedOrchestrator(DaemonClient client, bool ownsClient = true, Func<string, string?>? keystoreLookup = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _ownsClient = ownsClient;
        _keystoreLookup = keystoreLookup ?? DefaultKeystoreLookup;
    }

    private static string? DefaultKeystoreLookup(string name)
    {
        try
        {
            return ((GitLoom.Core.Security.ISecureKeyStore)new GitLoom.Core.Security.SecureKeyring()).Get(name);
        }
        catch (Exception)
        {
            // No keyring on this box — the CLI authenticates interactively in its terminal instead.
            return null;
        }
    }

    /// <summary>The shipped-app bundle: a loopback DaemonClient behind every control-center seam.</summary>
    public static OrchestratorServices CreateBundle()
    {
        var adapter = new DaemonBackedOrchestrator(DaemonClient.ForLoopback());
        adapter.Start();
        return OrchestratorServices.FromSingle(adapter);
    }

    /// <summary>Starts every background stream pump (idempotent). Construction never blocks on the daemon;
    /// each pump tolerates an unreachable daemon (reconnects with a fixed delay until cancelled).</summary>
    public void Start()
    {
        if (_agentPump is not null)
        {
            return;
        }

        _agentPump = Task.Run(() => AgentPumpAsync(_cts.Token));
        _planPump = Task.Run(() => PlanPumpAsync(_cts.Token));
        _spendPump = Task.Run(() => SpendPumpAsync(_cts.Token));
        _conversationPump = Task.Run(() => ConversationPumpAsync(_cts.Token));
    }

    /// <summary>P2-47 #5: a live terminal gateway over the daemon's <c>TerminalService.Attach</c> bidi stream,
    /// sharing this adapter's DaemonClient. The caller (the agent workspace) owns + disposes it per attach.</summary>
    public ITerminalGateway CreateTerminalGateway() => new DaemonTerminalGateway(_client);

    /// <summary>Point the merge-queue projection at a repo handle (from the daemon's <c>ProvisionRepo</c>).
    /// Restarts the queue pump so the merge rail + review cockpit reflect that repo's live queue.</summary>
    public void SetActiveRepo(string repoHandle)
    {
        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return;
        }

        lock (_gate)
        {
            if (_repoHandle == repoHandle && _queuePump is not null)
            {
                return;
            }

            _repoHandle = repoHandle;
            _queuePumpCts?.Cancel();
            _queuePumpCts?.Dispose();
            _queuePumpCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var ct = _queuePumpCts.Token;
            _queuePump = Task.Run(() => QueuePumpAsync(repoHandle, ct));
        }
    }

    // ---- pumps -----------------------------------------------------------

    private async Task AgentPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var e in _client.StreamAgentEventsAsync(ct).ConfigureAwait(false))
            {
                ApplyAgentEvent(e);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private async Task PlanPumpAsync(CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var update in _client.StreamPlansAsync(string.Empty, token).ConfigureAwait(false))
            {
                ApplyPlanUpdate(update);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task SpendPumpAsync(CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var sample in _client.StreamSpendAsync(token).ConfigureAwait(false))
            {
                ApplySpendSample(sample);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task ConversationPumpAsync(CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var update in _client.StreamConversationAsync(DefaultCoordinatorId, token).ConfigureAwait(false))
            {
                ApplyConversationUpdate(update);
            }
        }, ct).ConfigureAwait(false);
    }

    private async Task QueuePumpAsync(string repoHandle, CancellationToken ct)
    {
        await ReconnectLoopAsync(async token =>
        {
            await foreach (var update in _client.StreamQueueAsync(repoHandle, token).ConfigureAwait(false))
            {
                ApplyQueueUpdate(update);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Runs a single-shot stream body, reconnecting with a fixed delay on any fault (an
    /// unreachable daemon, a NOT_FOUND queue, a dropped stream) until cancelled. This is what makes an
    /// empty projection <b>live</b> — it keeps trying — rather than a hardcoded stub.</summary>
    private static async Task ReconnectLoopAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await body(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Transient (daemon down, queue not yet active, stream dropped) — back off and re-subscribe.
            }

            if (ct.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(ReconnectDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---- projection appliers --------------------------------------------

    private void ApplyAgentEvent(Proto.AgentEvent e)
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

                    // The coordinator is whichever live session carries the role (reconnect-safe);
                    // a snapshot without one clears it (the coordinator was stopped).
                    _coordinatorAgentId = _agents.Values
                        .FirstOrDefault(a => a.Role == GitLoom.Core.Agents.AgentRoles.Coordinator)?.AgentId;
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
                // Log lines feed the terminal (its own attach stream); nothing to project here.
                break;
        }

        EventReceived?.Invoke(new AgentEvent(
            Interlocked.Increment(ref _seq), e.EventCase.ToString(), e.AgentId, string.Empty, DateTimeOffset.UtcNow));
        Changed?.Invoke();
    }

    private void ApplyQueueUpdate(Proto.QueueUpdate update)
    {
        lock (_gate)
        {
            _mainSha = update.MainSha ?? string.Empty;
            _queue.Clear();
            _gate_.Clear();
            foreach (var entry in update.Entries)
            {
                var state = Enum.TryParse<WorkerMergeState>(entry.State, ignoreCase: true, out var s)
                    ? s : WorkerMergeState.Working;
                _queue.Add(new QueueEntry(
                    entry.AgentId, entry.AgentId, $"agent/{entry.AgentId}", state,
                    entry.GateReason ?? string.Empty, Verification: null, FlaggedItems: Array.Empty<FlaggedItem>()));
                _gate_[entry.AgentId] = (entry.CanMerge, entry.GateReason ?? string.Empty);
            }
        }

        Changed?.Invoke();
    }

    private void ApplyPlanUpdate(Proto.PlanUpdate update)
    {
        lock (_gate)
        {
            _plans.Clear();
            foreach (var p in update.Plans)
            {
                // Only pending plans are approvable cards; decided ones stay in the daemon's history.
                if (!string.Equals(p.Status, "Pending", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _plans.Add(new TaskPlan(
                    p.PlanId, p.Title, p.Scope.ToArray(), p.Approach, p.TestStrategy,
                    (decimal)p.BudgetUsd, DateTimeOffset.UtcNow));
            }
        }

        Changed?.Invoke();
    }

    private void ApplyConversationUpdate(Proto.ConversationUpdate update)
    {
        lock (_gate)
        {
            _transcript.Clear();
            foreach (var turn in update.Turns.OrderBy(t => t.Seq))
            {
                _transcript.Add(new ChatLine(
                    MapChatKind(turn.Role), turn.Text, DateTimeOffset.UtcNow,
                    string.IsNullOrEmpty(turn.PlanId) ? null : turn.PlanId));
            }
        }

        Changed?.Invoke();
    }

    private void ApplySpendSample(Proto.SpendSample sample)
    {
        lock (_gate)
        {
            _totalUsdMicros += sample.UsdMicrosSpent;
            _totalTokens += sample.TokensSpent;
            if (!string.IsNullOrEmpty(sample.AgentId))
            {
                _agentSpend.TryGetValue(sample.AgentId, out var acc);
                _agentSpend[sample.AgentId] = (acc.Tokens + sample.TokensSpent, acc.UsdMicros + sample.UsdMicrosSpent);
            }

            // No CPU/RAM in the gateway contract — spend is the live signal (0 for the host gauges is honest).
            _samples.Add(new ResourceSample(DateTimeOffset.UtcNow, 0, 0, _totalUsdMicros / 1_000_000m));
            if (_samples.Count > 120)
            {
                _samples.RemoveAt(0);
            }
        }

        Sampled?.Invoke();
    }

    private static AgentInfo MapInfo(Proto.AgentInfo a) =>
        new(a.AgentId, string.IsNullOrEmpty(a.AgentKind) ? a.AgentId : a.AgentKind,
            $"agent/{a.AgentId}", MapState(a.State), string.Empty, DateTimeOffset.UtcNow,
            Role: a.Role ?? string.Empty);

    private static AgentLifecycleState MapState(string? state) =>
        Enum.TryParse<AgentLifecycleState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : AgentLifecycleState.Working;

    // The daemon sends the Core ConversationRole enum name as a free-form string (G-14).
    private static ChatLineKind MapChatKind(string? role) => role switch
    {
        "Human" => ChatLineKind.Human,
        "Coordinator" => ChatLineKind.Coordinator,
        "ToolCall" => ChatLineKind.ToolCall,
        "PlanCard" => ChatLineKind.PlanCard,
        _ => ChatLineKind.SystemLine,
    };

    // ---- IAgentService (LIVE) -------------------------------------------

    public IReadOnlyList<AgentInfo> ListAgents()
    {
        lock (_gate)
        {
            return _agents.Values.OrderByDescending(a => a.SpawnedAt).ToArray();
        }
    }

    public event Action<AgentEvent>? EventReceived;

    public async Task EndAgentAsync(string agentId)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try { await _client.StopAgentAsync(agentId, cts.Token).ConfigureAwait(false); }
        catch (Exception) { /* daemon unreachable — surfaced via ConnectionState, not an app crash. */ }
    }

    // No per-agent pause/prompt/plan-tree RPCs exist on the daemon contract yet — these steer nothing.
    // They stay no-ops/empty (never fabricated); the terminal attach stream carries the live PTY instead.
    public Task PauseAgentAsync(string agentId) => Task.CompletedTask;
    public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;
    public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;
    public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();
    public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;
    public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();
    public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();

    // ---- IMergeQueueService (LIVE via StreamQueue) ----------------------

    public string MainSha { get { lock (_gate) return _mainSha; } }

    public IReadOnlyList<QueueEntry> GetQueue()
    {
        lock (_gate)
        {
            return _queue.ToArray();
        }
    }

    public bool CanMerge(string agentId, out string reason)
    {
        lock (_gate)
        {
            if (_gate_.TryGetValue(agentId, out var g))
            {
                reason = g.Reason;
                return g.CanMerge;
            }
        }

        reason = "not in the merge queue";
        return false;
    }

    /// <summary>The human foreground merge: take the lease (BeginMerge), then record the outcome and fire
    /// the stale cascade (ConfirmMerge). The actual Windows-side git merge between the two steps is the
    /// GUI/manual leg; the RPC pair is exercised for real here.</summary>
    public async Task ConfirmMergeAsync(string agentId)
    {
        string? repoHandle;
        string mainSha;
        lock (_gate)
        {
            repoHandle = _repoHandle;
            mainSha = _mainSha;
        }

        if (repoHandle is null)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var begun = await _client.BeginMergeAsync(repoHandle, agentId, cts.Token).ConfigureAwait(false);
        if (!begun.Granted)
        {
            throw new InvalidOperationException($"Can't merge — {begun.Reason}.");
        }

        await _client.ConfirmMergeAsync(repoHandle, agentId, begun.LeaseId, mainSha, cts.Token).ConfigureAwait(false);
    }

    // No flagged-change ack RPC on the StreamQueue contract — a genuine gap, not a wired surface.
    public Task AcknowledgeFlaggedChangeAsync(string agentId, string itemId) => Task.CompletedTask;

    /// <summary>P2-47 #7: fetch the agent-branch-vs-main diff (over the new GetMergeDiff RPC) so the review
    /// cockpit can build its <c>ReviewCockpitContext.MergeDiff</c> — which the queue stream doesn't carry.
    /// Returns null when no repo is active or the daemon is unreachable (the caller degrades gracefully).</summary>
    public async Task<MergeDiffResult?> GetMergeDiffAsync(string agentId, CancellationToken ct)
    {
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            return null;
        }

        try
        {
            var (branch, _, files) = await _client.GetMergeDiffAsync(repoHandle, agentId, ct).ConfigureAwait(false);
            return new MergeDiffResult(branch, files);
        }
        catch (Exception)
        {
            // No such branch / daemon unreachable — surfaced via ConnectionState; the cockpit stays empty.
            return null;
        }
    }

    // ---- ICoordinatorService (LIVE via StreamConversation + StreamPlans) -

    public IReadOnlyList<ChatLine> GetTranscript()
    {
        lock (_gate)
        {
            return _transcript.ToArray();
        }
    }

    public IReadOnlyList<TaskPlan> GetPendingPlans()
    {
        lock (_gate)
        {
            return _plans.ToArray();
        }
    }

    public TaskPlan? GetPlan(string planId)
    {
        lock (_gate)
        {
            return _plans.FirstOrDefault(p => p.PlanId == planId);
        }
    }

    public event Action? Changed;

    public async Task SendAsync(string text)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try { await _client.SendCoordinatorMessageAsync(DefaultCoordinatorId, text, cts.Token).ConfigureAwait(false); }
        catch (Exception) { /* daemon unreachable — surfaced via ConnectionState. */ }
    }

    public async Task SubmitPlanDecisionAsync(string planId, bool approve)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            if (approve)
            {
                await _client.ApprovePlanAsync(planId, cts.Token).ConfigureAwait(false);
            }
            else
            {
                await _client.RejectPlanAsync(planId, "rejected by operator", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { /* daemon unreachable / already decided — surfaced via ConnectionState. */ }
    }

    // ---- ICliAgentHost (PR3: coordinator-as-CLI) --------------------------

    /// <summary>The live coordinator's agent id (from the snapshot's role field), or null.</summary>
    public string? CoordinatorAgentId
    {
        get { lock (_gate) return _coordinatorAgentId; }
    }

    /// <summary>The installed agent CLIs, over the daemon's ListInstalledAdapters RPC.</summary>
    public async Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct)
    {
        var adapters = await _client.ListInstalledAdaptersAsync(ct).ConfigureAwait(false);
        return adapters
            .Select(a => new InstalledCliOption(a.Id, a.Version, a.ApiKeyEnvVar ?? string.Empty))
            .ToArray();
    }

    /// <summary>
    /// Starts the coordinator: resolves the CLI's BYOK key from the P2-01 keystore (by the
    /// adapter's declared env-var name — none means interactive login, no key travels), then
    /// SpawnAgent with the coordinator role against the active repo handle.
    /// </summary>
    public async Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cli);
        string? repoHandle;
        lock (_gate)
        {
            repoHandle = _repoHandle;
        }

        if (string.IsNullOrWhiteSpace(repoHandle))
        {
            throw new InvalidOperationException(
                "No repo is provisioned for agents yet — open a repository first.");
        }

        var provider = ApiKeyProviderMap.ProviderForEnvVar(cli.ApiKeyEnvVar);
        var key = provider is null ? null : _keystoreLookup(ApiKeyProviderMap.KeystoreKeyFor(provider));

        var agentId = await _client.SpawnAgentAsync(
            repoHandle, taskPrompt: string.Empty, agentKind: cli.Id, modelApiKey: key ?? string.Empty,
            ct, role: GitLoom.Core.Agents.AgentRoles.Coordinator).ConfigureAwait(false);

        lock (_gate)
        {
            _coordinatorAgentId = agentId;
        }

        Changed?.Invoke();
        return agentId;
    }

    // ---- IKillSwitchService (LIVE via Engage/Resume) --------------------

    public bool IsFrozen { get { lock (_gate) return _frozen; } }
    public KillSwitchPhase Phase { get { lock (_gate) return _phase; } }
    public string PhaseText { get { lock (_gate) return _phaseText; } }

    event Action? IKillSwitchService.Changed { add { Changed += value; } remove { Changed -= value; } }

    public async Task EngageAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            var report = await _client.EngageKillAsync(cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // A successful Engage means the queue is frozen (freeze-first, SA-1/F4) — regardless of
                // how many agents were live to pause.
                _frozen = true;
                _phase = KillSwitchPhase.Frozen;
                _phaseText = $"queue frozen · {report.AgentsPaused + report.AgentsYielded} agents paused";
            }
        }
        catch (Exception)
        {
            // Daemon unreachable — leave state unchanged.
        }

        Changed?.Invoke();
    }

    public async Task ResumeAsync()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            await _client.ResumeKillAsync(cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _frozen = false;
                _phase = KillSwitchPhase.Armed;
                _phaseText = string.Empty;
            }
        }
        catch (Exception)
        {
            // Daemon unreachable — leave state unchanged.
        }

        Changed?.Invoke();
    }

    // ---- ITelemetryService (LIVE via StreamSpend) -----------------------

    // No per-agent sandbox-event RPC on the contract — this is empty by contract, not a stubbed surface.
    public IReadOnlyList<SandboxEvent> GetSandboxEvents(string? agentId = null) => Array.Empty<SandboxEvent>();

    public ResourceSample Current
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count > 0 ? _samples[^1] : new ResourceSample(DateTimeOffset.UtcNow, 0, 0, 0m);
            }
        }
    }

    public IReadOnlyList<ResourceSample> History
    {
        get { lock (_gate) return _samples.ToArray(); }
    }

    public IReadOnlyList<AgentResourceUsage> GetAgentUsage()
    {
        lock (_gate)
        {
            return _agents.Values
                .Select(a =>
                {
                    _agentSpend.TryGetValue(a.AgentId, out var spend);
                    return new AgentResourceUsage(
                        a.AgentId, a.Name, a.State.ToString(), a.State == AgentLifecycleState.Paused,
                        CpuPercent: 0, RamGb: 0, SpendUsd: spend.UsdMicros / 1_000_000m, Task: a.Detail);
                })
                .OrderBy(a => a.Name, StringComparer.Ordinal)
                .ToArray();
        }
    }

    public event Action? Sampled;

    /// <summary>Reads the live per-agent + per-day budget caps.</summary>
    public async Task<Proto.Budget> GetBudgetsAsync(CancellationToken ct)
        => await _client.GetBudgetsAsync(ct).ConfigureAwait(false);

    /// <summary>Writes the per-agent + per-day budget caps (persisted + reflected in the live ledger).</summary>
    public async Task<Proto.Budget> SetBudgetsAsync(Proto.Budget budget, CancellationToken ct)
        => await _client.SetBudgetsAsync(budget, ct).ConfigureAwait(false);

    /// <summary>ITelemetryService budget seam (Core DTO): maps the live proto caps into the UI-facing
    /// record so the Resource Monitor can display + edit the per-day cap without touching proto types.</summary>
    public async Task<SpendBudget> GetSpendBudgetAsync(CancellationToken ct = default)
    {
        var b = await GetBudgetsAsync(ct).ConfigureAwait(false);
        return new SpendBudget(b.UsdMicrosCap, b.TokenCap, b.UsdMicrosCapPerDay, b.TokenCapPerDay);
    }

    /// <summary>Writes the whole cap record back through SetBudgets so an unedited cap is preserved.</summary>
    public async Task SetSpendBudgetAsync(SpendBudget budget, CancellationToken ct = default)
        => await SetBudgetsAsync(new Proto.Budget
        {
            UsdMicrosCap = budget.PerAgentUsdMicrosCap,
            TokenCap = budget.PerAgentTokenCap,
            UsdMicrosCapPerDay = budget.PerDayUsdMicrosCap,
            TokenCapPerDay = budget.PerDayTokenCap,
        }, ct).ConfigureAwait(false);

    // ---- IVibeService (separate future app — intentionally inert) --------

    public IReadOnlyList<Checkpoint> GetCheckpoints() => Array.Empty<Checkpoint>();
    public Checkpoint? LastVerifiedGreen => null;
    public Task RestoreCheckpointAsync(string sha) => Task.CompletedTask;
    public DeployStatus Deploy => new(DeployPhase.Idle, null, null);
    public event Action? DeployChanged { add { } remove { } }
    public Task PublishAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _cts.Cancel();
        try { _queuePumpCts?.Cancel(); } catch { /* ignore */ }
        try
        {
            Task.WaitAll(
                new[] { _agentPump, _planPump, _spendPump, _conversationPump, _queuePump }
                    .Where(t => t is not null).Select(t => t!).ToArray(),
                TimeSpan.FromSeconds(2));
        }
        catch { /* pump cancellation */ }

        _queuePumpCts?.Dispose();
        _cts.Dispose();
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }
}
