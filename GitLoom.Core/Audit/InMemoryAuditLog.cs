using System.Collections.Generic;

namespace GitLoom.Core.Audit;

/// <summary>
/// A thread-safe in-memory <see cref="IAuditLog"/> — the pre-P2-15 journal. Used by
/// the daemon skeleton and by tests (through <c>AuditProbe</c>). P2-15 replaces this
/// with the hash-chained, persisted implementation behind the same interface.
/// </summary>
public sealed class InMemoryAuditLog : IAuditLog
{
    private readonly List<AuditEvent> _events = new();
    private readonly object _gate = new();

    public void Append(AuditEvent auditEvent)
    {
        lock (_gate)
        {
            _events.Add(auditEvent);
        }
    }

    public IReadOnlyList<AuditEvent> Read()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }
}
