using System;

namespace Mainguard.Git.Models;

/// <summary>
/// One persisted line of model-API spend (P2-08 budget ledger). Written by the gateway when a lease
/// settles with actual token usage and streamed to the client over <c>GatewayService.StreamSpend</c>.
/// Each row carries <see cref="AgentId"/> so P2-10 can join spend to merged tasks
/// (cost-per-merged-change) via <c>BudgetLedger.GetSpendSince</c>.
/// </summary>
public class SpendRecord
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The agent this spend is attributed to.</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>The model id the spend priced against (drives the per-model cost lookup).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Tokens consumed by the settled request (actuals, not the estimate).</summary>
    public long Tokens { get; set; }

    /// <summary>Cost in USD micro-dollars (millionths of a dollar) from the static price table.</summary>
    public long UsdMicros { get; set; }

    /// <summary>When the spend was recorded (UTC).</summary>
    public DateTime WhenUtc { get; set; }
}
