using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Audit;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Security;

namespace GitLoom.Core.Agents;

/// <summary>One granted gateway slice: the agent, the bucket ticket, and the reserved token estimate.</summary>
public sealed record GatewayLease(string AgentId, long Ticket, int EstimatedTokens);

/// <summary>Per-agent view for <see cref="IAiGateway.GetSnapshot"/>: spend, queue depth, and state.</summary>
public sealed record AgentSpendSnapshot(
    string AgentId, long Tokens, long UsdMicros, int QueueDepth, string State, string? Reason);

/// <summary>The gateway snapshot: per-agent spend + queue depth, and the current shared key limits.</summary>
public sealed record GatewaySnapshot(
    IReadOnlyList<AgentSpendSnapshot> Agents,
    int TotalQueueDepth,
    double RequestsPerMinute,
    double TokensPerMinute);

/// <summary>The daemon-side hook the gateway uses to pause/resume a worker and reflect its state.</summary>
public interface IAgentSupervisor
{
    /// <summary>Pause the worker's PTY input so its CLI stops issuing new work (429 / budget pause).</summary>
    void PauseInput(string agentId);

    /// <summary>Resume the worker's PTY input after a rate-limit window clears.</summary>
    void ResumeInput(string agentId);

    /// <summary>Reflect the agent's gateway state in <c>ListAgents</c> metadata (e.g. <c>RateLimited</c>).</summary>
    void MarkState(string agentId, string state, string? reason);
}

/// <summary>A no-op supervisor for contexts with no worker attached (e.g. pure unit paths).</summary>
public sealed class NullAgentSupervisor : IAgentSupervisor
{
    public static NullAgentSupervisor Instance { get; } = new();

    public void PauseInput(string agentId) { }

    public void ResumeInput(string agentId) { }

    public void MarkState(string agentId, string state, string? reason) { }
}

/// <summary>
/// P2-08 AI gateway (contract, exact). Sits on the egress path so N agents share one key without any
/// agent ever seeing a raw 429: <see cref="AcquireAsync"/> blocks FIFO on the shared
/// <see cref="TokenBucket"/>, <see cref="Report429"/> pauses the worker and backs off, and spend is
/// settled through the <see cref="BudgetLedger"/>.
/// </summary>
public interface IAiGateway
{
    /// <summary>FIFO (within a priority class) acquire of one request's rate budget.</summary>
    Task<GatewayLease> AcquireAsync(string agentId, int estimatedTokens, CancellationToken ct);

    /// <summary>Signal an upstream 429: pause the worker, mark <c>RateLimited</c>, start backoff.</summary>
    void Report429(string agentId, TimeSpan? retryAfter);

    /// <summary>Per-agent spend, queue depth, and current limits.</summary>
    GatewaySnapshot GetSnapshot();
}

/// <summary>
/// The concrete gateway. Composes a shared <see cref="TokenBucket"/> (seeded from P2-01 key health)
/// and a <see cref="BudgetLedger"/>, and drives the 429 pause/backoff/resume dance through an injected
/// <see cref="IAgentSupervisor"/>. Budget exhaustion <b>pauses</b> the agent with a typed reason (and
/// a <c>budget_exceeded</c> audit event) — it never kills the container (rejection trigger).
///
/// <para>The <see cref="IAiGateway"/> surface is exactly the contract; the extra members
/// (<see cref="Settle"/>, <see cref="RemainingBackoff"/>, <see cref="ClearRateLimit"/>, <see cref="Pump"/>)
/// are the daemon-side forwarding seam used by <c>ModelProxyMiddleware</c>.</para>
/// </summary>
public sealed class AiGateway : IAiGateway
{
    private readonly TokenBucket _bucket;
    private readonly BudgetLedger _ledger;
    private readonly IAgentSupervisor _supervisor;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentEntry> _agents = new(StringComparer.Ordinal);

    public AiGateway(
        TokenBucket bucket,
        BudgetLedger ledger,
        IAgentSupervisor? supervisor = null,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null)
    {
        _bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _supervisor = supervisor ?? NullAgentSupervisor.Instance;
        _audit = audit ?? new InMemoryAuditLog();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Convenience ctor: seed the bucket from key health and use an in-memory spend store.</summary>
    public static AiGateway Create(
        KeyHealth? health,
        Func<DateTimeOffset> clock,
        IAgentSupervisor? supervisor = null,
        IAuditLog? audit = null,
        BudgetCaps? caps = null)
    {
        var bucket = TokenBucket.FromKeyHealth(health, clock);
        var ledger = new BudgetLedger(new InMemorySpendStore(), clock, caps);
        return new AiGateway(bucket, ledger, supervisor, audit, clock);
    }

    /// <summary>The composed budget ledger (spend recording + caps + <c>StreamSpend</c> source).</summary>
    public BudgetLedger Ledger => _ledger;

    public async Task<GatewayLease> AcquireAsync(string agentId, int estimatedTokens, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("agentId is required.", nameof(agentId));
        }

        // Budget gate first: an exhausted agent is paused with a typed reason and never forwards more.
        if (_ledger.IsExhausted(agentId, out var reason))
        {
            MarkBudgetExhausted(agentId, reason);
            throw new BudgetExhaustedException(agentId, reason);
        }

        AdjustPending(agentId, +1);
        try
        {
            var bucketLease = await _bucket.AcquireAsync(estimatedTokens, ct).ConfigureAwait(false);
            return new GatewayLease(agentId, bucketLease.Ticket, bucketLease.EstimatedTokens);
        }
        finally
        {
            AdjustPending(agentId, -1);
        }
    }

    public void Report429(string agentId, TimeSpan? retryAfter)
    {
        lock (_gate)
        {
            var entry = GetOrAddLocked(agentId);
            entry.RateLimitAttempts++;
            var delay = GatewayBackoff.Compute(entry.RateLimitAttempts, retryAfter);
            entry.BackoffUntil = _clock() + delay;
            entry.State = "RateLimited";
            entry.Reason = retryAfter is { } ra
                ? $"Rate limited; retrying after ~{ra.TotalSeconds:0}s."
                : $"Rate limited; backing off ~{delay.TotalSeconds:0}s.";
        }

        _supervisor.PauseInput(agentId);
        _supervisor.MarkState(agentId, "RateLimited", CurrentReason(agentId));
    }

    /// <summary>Remaining backoff for an agent (0 when the window has elapsed). Drives the retry wait.</summary>
    public TimeSpan RemainingBackoff(string agentId)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(agentId, out var entry) && entry.BackoffUntil is { } until)
            {
                var remaining = until - _clock();
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }

            return TimeSpan.Zero;
        }
    }

    /// <summary>Clear an agent's rate-limit state and resume its worker after a successful retry.</summary>
    public void ClearRateLimit(string agentId)
    {
        var wasRateLimited = false;
        lock (_gate)
        {
            if (_agents.TryGetValue(agentId, out var entry) && entry.State == "RateLimited")
            {
                entry.RateLimitAttempts = 0;
                entry.BackoffUntil = null;
                entry.State = "Running";
                entry.Reason = null;
                wasRateLimited = true;
            }
        }

        if (wasRateLimited)
        {
            _supervisor.ResumeInput(agentId);
            _supervisor.MarkState(agentId, "Running", null);
        }
    }

    /// <summary>
    /// Settle a lease with actual token usage: reconcile the bucket (estimate→actual conserved) and
    /// record the spend row (priced by <paramref name="model"/>). Streams via the ledger event.
    /// </summary>
    public SpendTotals Settle(GatewayLease lease, int actualTokens, string model)
    {
        _bucket.Release(new BucketLease(lease.Ticket, lease.EstimatedTokens), actualTokens);
        _ledger.Record(lease.AgentId, model, actualTokens);
        return _ledger.GetTotals(lease.AgentId);
    }

    /// <summary>Re-evaluate the bucket's FIFO waiter queue (the daemon pump-loop cadence calls this).</summary>
    public int Pump() => _bucket.Pump();

    /// <summary>
    /// The daemon pump loop: periodically grants any bucket waiters whose capacity has refilled. Runs
    /// until <paramref name="ct"/> is cancelled. The interval is poll frequency only — the bucket math
    /// still uses the injected clock.
    /// </summary>
    public async Task RunPumpLoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _bucket.Pump();
        }
    }

    public GatewaySnapshot GetSnapshot()
    {
        var (rpm, tpm) = _bucket.Capacity;
        var totalQueue = _bucket.QueueDepth;

        var ids = new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            foreach (var id in _agents.Keys)
            {
                ids.Add(id);
            }
        }

        foreach (var row in _ledger.AllRows())
        {
            ids.Add(row.AgentId);
        }

        var agents = new List<AgentSpendSnapshot>(ids.Count);
        foreach (var id in ids.OrderBy(x => x, StringComparer.Ordinal))
        {
            var totals = _ledger.GetTotals(id);
            int pending;
            string state;
            string? reason;
            lock (_gate)
            {
                if (_agents.TryGetValue(id, out var entry))
                {
                    pending = entry.Pending;
                    state = entry.State;
                    reason = entry.Reason;
                }
                else
                {
                    pending = 0;
                    state = "Running";
                    reason = null;
                }
            }

            agents.Add(new AgentSpendSnapshot(id, totals.Tokens, totals.UsdMicros, pending, state, reason));
        }

        return new GatewaySnapshot(agents, totalQueue, rpm, tpm);
    }

    private void MarkBudgetExhausted(string agentId, string reason)
    {
        bool firstTime;
        lock (_gate)
        {
            var entry = GetOrAddLocked(agentId);
            firstTime = entry.State != "BudgetExhausted";
            entry.State = "BudgetExhausted";
            entry.Reason = reason;
        }

        _supervisor.PauseInput(agentId);
        _supervisor.MarkState(agentId, "BudgetExhausted", reason);

        if (firstTime)
        {
            _audit.Append(new AuditEvent("budget_exceeded", new Dictionary<string, string>
            {
                ["agent_id"] = agentId,
                ["reason"] = reason,
            }));
        }
    }

    private void AdjustPending(string agentId, int delta)
    {
        lock (_gate)
        {
            var entry = GetOrAddLocked(agentId);
            entry.Pending = Math.Max(0, entry.Pending + delta);
        }
    }

    private string? CurrentReason(string agentId)
    {
        lock (_gate)
        {
            return _agents.TryGetValue(agentId, out var entry) ? entry.Reason : null;
        }
    }

    private AgentEntry GetOrAddLocked(string agentId)
    {
        if (!_agents.TryGetValue(agentId, out var entry))
        {
            entry = new AgentEntry();
            _agents[agentId] = entry;
        }

        return entry;
    }

    private sealed class AgentEntry
    {
        public int Pending { get; set; }

        public string State { get; set; } = "Running";

        public string? Reason { get; set; }

        public int RateLimitAttempts { get; set; }

        public DateTimeOffset? BackoffUntil { get; set; }
    }
}

/// <summary>
/// Pure exponential backoff that <b>honors <c>Retry-After</c> as a floor</b> (P2-08). Attempt is
/// 1-based; the base doubles (1s, 2s, 4s, …) and is capped, and the greater of that and any provider
/// <c>Retry-After</c> wins. Deterministic — the <c>Backoff_HonorsRetryAfter</c> test asserts a
/// <c>Retry-After: 5</c> resumes at ≈5 s on a virtual clock.
/// </summary>
public static class GatewayBackoff
{
    /// <summary>Base delay for attempt 1 before doubling.</summary>
    public static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound so runaway backoff cannot exceed a couple of minutes.</summary>
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(2);

    /// <summary>The delay for <paramref name="attempt"/> (1-based), never below <paramref name="retryAfter"/>.</summary>
    public static TimeSpan Compute(int attempt, TimeSpan? retryAfter, TimeSpan? maxCap = null)
    {
        var cap = maxCap ?? MaxDelay;
        var clampedAttempt = Math.Max(1, attempt);
        // 2^(attempt-1) * base, guarding against overflow at high attempt counts.
        var exponent = Math.Min(clampedAttempt - 1, 20);
        var exponential = TimeSpan.FromTicks(BaseDelay.Ticks * (1L << exponent));
        if (exponential > cap)
        {
            exponential = cap;
        }

        if (retryAfter is { } floor && floor > exponential)
        {
            return floor <= cap ? floor : cap;
        }

        return exponential;
    }
}
