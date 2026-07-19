namespace Mainguard.Git.Models;

/// <summary>
/// The persisted gateway budget caps (P2-08) — a single row set via <c>GatewayService.SetBudgets</c>
/// and read by <c>GetBudgets</c>. Maps to the proto <c>Budget</c> (per-agent token + USD-micro caps).
/// </summary>
public class GatewayBudget
{
    /// <summary>Fixed primary key (there is a single budget row).</summary>
    public int Id { get; set; } = 1;

    /// <summary>Per-agent USD micro-dollar cap (0 = unlimited).</summary>
    public long UsdMicrosCap { get; set; }

    /// <summary>Per-agent token cap (0 = unlimited).</summary>
    public long TokenCap { get; set; }

    /// <summary>Per-day USD micro-dollar cap across all agents (0 = unlimited). P2-13 carried-in from
    /// P2-08 — enforced daemon-side in <c>BudgetCaps.PerDayUsdMicrosCap</c>, now persisted + editable.</summary>
    public long UsdMicrosCapPerDay { get; set; }

    /// <summary>Per-day token cap across all agents (0 = unlimited).</summary>
    public long TokenCapPerDay { get; set; }
}
