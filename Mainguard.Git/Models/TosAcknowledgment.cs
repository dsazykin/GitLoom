using System;

namespace Mainguard.Git.Models;

/// <summary>
/// A recorded acknowledgment that the user understands a provider's terms (P2-01) — specifically the
/// Anthropic subscription-OAuth restriction enforced 2026-04-04, shown before the CLI-OAuth path can
/// be selected. Persisted in SQLite via <see cref="AppDbContext"/> so it survives restarts (invariant 4);
/// P2-15 chains off <see cref="AppDbContext.HasTosAcknowledgment"/>.
/// </summary>
public sealed class TosAcknowledgment
{
    public int Id { get; set; }

    /// <summary>The provider the acknowledgment covers (e.g. "anthropic").</summary>
    public string Provider { get; set; } = "";

    /// <summary>When the user acknowledged, in UTC-aware form.</summary>
    public DateTimeOffset AcknowledgedAt { get; set; }
}
