using System;
using System.Collections.Concurrent;
using Mainguard.Agents.Agents;

namespace GitLoom.Server.Runtime;

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
        previous?.Dispose();
    }

    /// <summary>The agent's live bound session, or null when none is registered.</summary>
    public BoundTerminalSession? TryGetBound(string agentId) =>
        agentId is not null && _bound.TryGetValue(agentId, out var session) ? session : null;

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
