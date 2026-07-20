using System;
using System.Collections.Concurrent;

namespace Mainguard.Server.Auth;

/// <summary>
/// Tracks which agents' terminals are <b>Locked</b> (P2-14). A managed worker (spawned in coordinated
/// mode from an approved plan) is locked: the <see cref="RoleInterceptor"/> severs its <c>Attach</c> INPUT
/// frames at the gRPC layer (read streams still flow), so terminal locking is enforced daemon-side, never
/// UI-only (the master doc's explicit "not just UI read-only" invariant — test 5). Manual-mode agents are
/// unlocked.
/// </summary>
public sealed class TerminalLockRegistry
{
    private readonly ConcurrentDictionary<string, byte> _locked = new(StringComparer.Ordinal);

    /// <summary>Lock a managed worker's terminal input (coordinated-mode spawn).</summary>
    public void Lock(string agentId)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            _locked[agentId] = 0;
        }
    }

    /// <summary>Unlock (teardown / mode change).</summary>
    public void Unlock(string agentId) => _locked.TryRemove(agentId, out _);

    /// <summary>True when the agent's terminal input is severed.</summary>
    public bool IsLocked(string agentId) => agentId is not null && _locked.ContainsKey(agentId);
}
