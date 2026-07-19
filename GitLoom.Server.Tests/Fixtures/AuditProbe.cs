using System;
using System.Linq;
using Mainguard.Git.Audit;

namespace GitLoom.Server.Tests.Fixtures;

/// <summary>
/// TI-P2-00 §A.4.6 — wraps an <see cref="IAuditLog"/> and asserts appended events by
/// type and order. Every G-17 touchpoint test asserts through this — one event per
/// operation, by type, never by log-text grep. P2-15 swaps the underlying log for the
/// hash-chained implementation behind the same <see cref="IAuditLog"/> seam; this probe
/// is unchanged by that.
/// </summary>
public sealed class AuditProbe
{
    private readonly IAuditLog _log;

    public AuditProbe(IAuditLog log) => _log = log;

    /// <summary>The event types appended so far, in order.</summary>
    public string[] Types => _log.Read().Select(e => e.Type).ToArray();

    /// <summary>Asserts the appended event types equal exactly <paramref name="types"/>, in order.</summary>
    public void AssertSequence(params string[] types)
    {
        var actual = Types;
        if (!actual.SequenceEqual(types))
        {
            throw new Xunit.Sdk.XunitException(
                $"Audit sequence mismatch.\n  expected: [{string.Join(", ", types)}]\n  actual:   [{string.Join(", ", actual)}]");
        }
    }

    /// <summary>
    /// Asserts exactly one appended event of <paramref name="type"/> matches
    /// <paramref name="predicate"/> (G-17 idempotence: one event per operation).
    /// </summary>
    public void AssertExactlyOne(string type, Func<AuditEvent, bool> predicate)
    {
        var matches = _log.Read().Where(e => e.Type == type && predicate(e)).ToArray();
        if (matches.Length != 1)
        {
            throw new Xunit.Sdk.XunitException(
                $"Expected exactly one '{type}' event matching the predicate, found {matches.Length}.");
        }
    }
}
