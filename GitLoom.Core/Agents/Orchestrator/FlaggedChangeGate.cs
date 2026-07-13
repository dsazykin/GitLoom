using System;
using System.Collections.Concurrent;
using GitLoom.Core.Audit;
using GitLoom.Core.Review;

namespace GitLoom.Core.Agents.Orchestrator;

/// <summary>
/// The P2-11 flagged-changes merge gate: a composable <see cref="IMergeGate"/> the queue ANDs into
/// <see cref="IMergeQueue.CanMerge"/>. It holds a per-agent <see cref="AcknowledgmentStore"/>; a branch may
/// merge only once every flagged item for its <b>current</b> flagged-set hash is acknowledged item-by-item
/// (a global ack is impossible — the store exposes no "ack all"). This is the security boundary the review
/// cockpit renders and drives: hostile agent code lands in a flag-worthy category (ExecutableConfig, CI,
/// hooks, security paths, F6 out-of-scope, RT-D2), and the human must acknowledge each before merge.
/// </summary>
public sealed class FlaggedChangeGate : IMergeGate
{
    private readonly ConcurrentDictionary<string, AcknowledgmentStore> _stores = new(StringComparer.Ordinal);
    private readonly IAuditLog _audit;

    /// <param name="audit">Audit sink threaded into every per-agent store (for <c>acknowledged_flagged_change</c>).</param>
    public FlaggedChangeGate(IAuditLog? audit = null) => _audit = audit ?? new InMemoryAuditLog();

    /// <summary>The acknowledgment store for an agent (created on first use). The cockpit sets its flagged set and acks items.</summary>
    public AcknowledgmentStore StoreFor(string agentId) =>
        _stores.GetOrAdd(agentId ?? string.Empty, id => new AcknowledgmentStore(id, _audit));

    public bool Allows(string agentId, out string reason)
    {
        if (!_stores.TryGetValue(agentId ?? string.Empty, out var store) || store.AllAcknowledged)
        {
            reason = "";
            return true;
        }

        var pending = store.PendingCount;
        reason = pending == 1
            ? "1 flagged change needs acknowledgment"
            : $"{pending} flagged changes need acknowledgment";
        return false;
    }
}
