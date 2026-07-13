using System;
using GitLoom.Core.Agents;

namespace GitLoom.Server.Runtime;

/// <summary>
/// Owns the mapping from an agent id to its live <see cref="PtySession"/>. In the interim P2-03
/// engine no agent process is bound yet — that arrives with the P2-09 agent lifecycle — so the
/// default daemon registers this <b>without a factory</b> and <see cref="Create"/> returns
/// <c>null</c>; <see cref="Services.TerminalGrpcService"/> then falls back to the P2-02 echo so an
/// attach still round-trips. P2-09 (and the wiring tests) supply a real-PTY factory, at which point
/// <see cref="Services.TerminalGrpcService"/> drives the session through <see cref="Terminal.TerminalStreamer"/>.
///
/// <para>Kept as host state separate from the transport (the P2-02 rejection trigger: no business
/// logic in the gRPC classes).</para>
/// </summary>
public sealed class TerminalSessionManager
{
    private readonly Func<string, PtySession>? _factory;

    /// <summary>Interim registration: no PTY factory, so attaches echo until P2-09 binds agents.</summary>
    public TerminalSessionManager()
    {
    }

    /// <summary>PTY-backed registration: <paramref name="factory"/> spawns the session for an agent id.</summary>
    public TerminalSessionManager(Func<string, PtySession> factory)
    {
        _factory = factory;
    }

    /// <summary>Whether a PTY factory is configured (i.e. attaches drive a real PTY, not the echo fallback).</summary>
    public bool CanSpawn => _factory is not null;

    /// <summary>Spawns a PTY session for <paramref name="agentId"/>, or <c>null</c> when no factory is configured.</summary>
    public PtySession? Create(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new ArgumentException("An agent id is required.", nameof(agentId));
        }

        return _factory?.Invoke(agentId);
    }
}
