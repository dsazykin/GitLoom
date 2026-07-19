using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;

namespace Mainguard.Server.Runtime;

/// <summary>
/// The interim daemon <see cref="IKillTarget"/> over the <see cref="AgentSessionStore"/>. Until the P2-09
/// yield/pause substrate is bound into the host, a kill marks every live session <c>Paused</c> (the
/// recoverable state) and reports it — the freeze-first ordering, the RT-D4 timing, and the snapshot are
/// all owned by <see cref="KillSwitch"/> regardless of this target. The real target (cooperative yield →
/// <c>docker pause</c>) swaps in behind the same seam.
/// </summary>
public sealed class SessionStoreKillTarget : IKillTarget
{
    private readonly AgentSessionStore _store;

    public SessionStoreKillTarget(AgentSessionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public IReadOnlyList<string> ActiveAgentIds => _store.List().Select(s => s.Id).ToList();

    public Task<bool> RequestYieldAsync(string agentId, TimeSpan timeout, CancellationToken ct)
    {
        // No cooperative-yield channel is bound in the interim host, so report "did not yield" and let the
        // KillSwitch take the pause fallback (which is what actually stops the work).
        return Task.FromResult(false);
    }

    public Task PauseAsync(string agentId, CancellationToken ct)
    {
        _store.MarkState(agentId, "Paused", "Kill switch engaged — paused (recoverable).");
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, string> CaptureStates() =>
        _store.List().ToDictionary(s => s.Id, s => s.State, StringComparer.Ordinal);
}
