using System.Collections.Generic;

namespace Mainguard.Git.Audit;

/// <summary>
/// The minimal append/read audit seam (G-17). Every agent-initiated ref mutation,
/// spawn/kill, plan approval, and merge decision appends one <see cref="AuditEvent"/>
/// here. Before P2-15 lands this is a plain ordered journal; P2-15 supplies the
/// hash-chained, tamper-evident implementation behind this same interface — so this
/// seam is deliberately narrow (append + read) and carries no chaining/crypto concept.
/// The Phase-2 test <c>AuditProbe</c> fixture wraps this interface.
/// </summary>
public interface IAuditLog
{
    /// <summary>Appends one event. Implementations preserve append order.</summary>
    void Append(AuditEvent auditEvent);

    /// <summary>All appended events, oldest first.</summary>
    IReadOnlyList<AuditEvent> Read();
}

/// <summary>
/// One audit record: a stable <paramref name="Type"/> discriminator (e.g. "spawn",
/// "killswitch", "plan_approved") plus opaque string fields. Kept intentionally flat
/// and string-typed so P2-15's hash chain can serialize it deterministically without
/// this seam having to know the chain format.
/// </summary>
public sealed record AuditEvent(string Type, IReadOnlyDictionary<string, string> Fields)
{
    public AuditEvent(string type) : this(type, new Dictionary<string, string>()) { }
}
