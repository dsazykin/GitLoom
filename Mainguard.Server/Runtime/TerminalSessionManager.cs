using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;

namespace Mainguard.Server.Runtime;

/// <summary>
/// Owns the mapping from an agent id to its live terminal session. Two registration shapes:
///
/// <para><b>Bound sessions (the real agent path):</b> when a spawn launches an installed CLI in its
/// jail, <see cref="AgentCliBinder"/> registers the resulting long-lived
/// <see cref="BoundTerminalSession"/> here via <see cref="Bind"/>. Attaches subscribe to the live
/// session (replay + fanout) and a detach never kills the CLI —
/// <see cref="Services.TerminalGrpcService"/> streams the REAL CLI, not the echo.</para>
///
/// <para><b>Per-attach factory (legacy/test shape):</b> a <see cref="PtySession"/> factory spawns a
/// fresh child per attach that dies on detach — the shape the TI-P2-03 wiring tests inject. With
/// neither a bound session nor a factory, the attach falls back to the P2-02 echo.</para>
///
/// <para>Kept as host state separate from the transport (the P2-02 rejection trigger: no business
/// logic in the gRPC classes).</para>
/// </summary>
public sealed class TerminalSessionManager : IDisposable
{
    private readonly Func<string, PtySession>? _factory;
    private readonly ConcurrentDictionary<string, BoundTerminalSession> _bound = new(StringComparer.Ordinal);
    // Agents whose spawn is in-flight and WILL bind a CLI shortly (container start + docker-exec-under-PTY
    // takes a few seconds). A client attaches the instant the agent appears ("Starting"), which is BEFORE
    // that bind — so the attach waits on this rather than latching into echo (the attach-before-bind race).
    private readonly ConcurrentDictionary<string, byte> _bindPending = new(StringComparer.Ordinal);

    /// <summary>How long an attach waits for a pending bind before giving up (echo). The bind normally
    /// lands ~5 s after spawn; the ceiling covers a cold container start. Settable low by tests.</summary>
    public static TimeSpan BindWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Poll cadence while waiting for a pending bind (a cheap concurrent-dictionary read).</summary>
    public static TimeSpan BindWaitPollInterval { get; set; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Default registration: no per-attach factory; only bound sessions stream a real PTY.</summary>
    public TerminalSessionManager()
    {
    }

    /// <summary>PTY-backed registration: <paramref name="factory"/> spawns the session for an agent id.</summary>
    public TerminalSessionManager(Func<string, PtySession> factory)
    {
        _factory = factory;
    }

    /// <summary>Whether a per-attach PTY factory is configured.</summary>
    public bool CanSpawn => _factory is not null;

    /// <summary>Spawns a per-attach PTY session for <paramref name="agentId"/>, or <c>null</c> when no factory is configured.</summary>
    public PtySession? Create(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        return _factory?.Invoke(agentId);
    }

    /// <summary>
    /// Registers the agent's long-lived CLI session. A previous binding for the same agent (a
    /// re-spawn into a reused jail) is killed and replaced — one CLI per agent id.
    /// </summary>
    public void Bind(string agentId, BoundTerminalSession session)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        ArgumentNullException.ThrowIfNull(session);
        var previous = _bound.TryGetValue(agentId, out var existing) ? existing : null;
        _bound[agentId] = session;
        _bindPending.TryRemove(agentId, out _); // the bind we were waiting for landed
        previous?.Dispose();
    }

    /// <summary>The agent's live bound session, or null when none is registered.</summary>
    public BoundTerminalSession? TryGetBound(string agentId) =>
        agentId is not null && _bound.TryGetValue(agentId, out var session) ? session : null;

    /// <summary>Records that <paramref name="agentId"/>'s spawn is in-flight and a CLI bind is expected —
    /// set at session creation, so an attach arriving during the spawn waits for the bind. Cleared by
    /// <see cref="Bind"/> (bind landed) or <see cref="ClearBindPending"/> (session-only / bind failed).</summary>
    public void MarkBindPending(string agentId)
    {
        if (!string.IsNullOrWhiteSpace(agentId)) _bindPending[agentId] = 0;
    }

    /// <summary>Clears the pending-bind flag: this agent is session-only (no CLI) or its bind failed, so an
    /// attach should stop waiting and fall back to echo/PTY. Idempotent.</summary>
    public void ClearBindPending(string agentId)
    {
        if (agentId is not null) _bindPending.TryRemove(agentId, out _);
    }

    /// <summary>Whether a CLI bind is still expected for <paramref name="agentId"/> (spawn in-flight).</summary>
    public bool IsBindPending(string agentId) => agentId is not null && _bindPending.ContainsKey(agentId);

    /// <summary>
    /// Waits for <paramref name="agentId"/>'s CLI to bind, returning its session — the fix for the
    /// attach-before-bind race that left a coordinator terminal in echo. Returns as soon as a bound
    /// session appears; returns null when the pending-bind flag clears without one (session-only / bind
    /// failed), the <see cref="BindWaitTimeout"/> elapses, or the caller cancels.
    /// </summary>
    public async Task<BoundTerminalSession?> WaitForBoundAsync(string agentId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + BindWaitTimeout;
        while (true)
        {
            if (TryGetBound(agentId) is { } bound) return bound;
            if (!IsBindPending(agentId)) return null;                 // no bind coming after all
            if (DateTimeOffset.UtcNow >= deadline) return null;       // took too long — degrade to echo

            try { await Task.Delay(BindWaitPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }        // client detached
        }
    }

    /// <summary>Kills and unregisters the agent's bound session (StopAgent / teardown). Idempotent.</summary>
    public void Release(string agentId)
    {
        if (agentId is not null && _bound.TryRemove(agentId, out var session))
        {
            session.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var agentId in _bound.Keys)
        {
            Release(agentId);
        }
    }
}
