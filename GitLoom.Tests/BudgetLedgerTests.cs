using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using Mainguard.Git.Audit;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Security;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-08 test contract #4/#8 — budgets, spend telemetry, and the typed-pause-not-kill invariant.
/// </summary>
public class BudgetLedgerTests
{
    private static Func<DateTimeOffset> FrozenClock(DateTimeOffset at) => () => at;

    [Fact]
    public void ModelPriceTable_PricesKnownAndUnknownModels()
    {
        // 1,000,000 haiku tokens ≈ $1.00 = 1,000,000 micros.
        Assert.Equal(1_000_000, ModelPriceTable.CostMicros("claude-3-5-haiku", 1_000_000));
        // Prefix match: dated model id maps to its family price.
        Assert.Equal(6_000_000, ModelPriceTable.CostMicros("claude-3-5-sonnet-20241022", 1_000_000));
        // Unknown model → conservative default rate.
        Assert.Equal(ModelPriceTable.DefaultUsdMicrosPerMillionTokens, ModelPriceTable.CostMicros("mystery-model", 1_000_000));
    }

    [Fact]
    public void IsExhausted_TripsOnPerAgentTokenCap_WithHonestReason()
    {
        var ledger = new BudgetLedger(new InMemorySpendStore(), FrozenClock(DateTimeOffset.UtcNow),
            new BudgetCaps(PerAgentTokenCap: 1000, 0, 0, 0));

        ledger.Record("agent-1", "claude-3-5-haiku", 600);
        Assert.False(ledger.IsExhausted("agent-1", out _));

        ledger.Record("agent-1", "claude-3-5-haiku", 500); // 1100 ≥ 1000
        Assert.True(ledger.IsExhausted("agent-1", out var reason));
        Assert.Contains("1,000", reason); // states the cap
        // Another agent is unaffected.
        Assert.False(ledger.IsExhausted("agent-2", out _));
    }

    [Fact]
    public void GetSpendSince_FiltersByAgentAndTime()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var now = t0;
        var ledger = new BudgetLedger(new InMemorySpendStore(), () => now, BudgetCaps.Unlimited);

        ledger.Record("a", "gpt-4o", 100);
        now = t0.AddHours(2);
        var since = now;
        ledger.Record("a", "gpt-4o", 250);
        ledger.Record("b", "gpt-4o", 999);

        var spend = ledger.GetSpendSince("a", since);
        Assert.Equal(250, spend.Tokens); // the earlier 100 and agent b are excluded
    }

    [Fact]
    public async Task Budget_ExhaustionPausesTyped_NotKilled_AndAudits()
    {
        var audit = new InMemoryAuditLog();
        var supervisor = new FakeAgentSupervisor();
        var gateway = AiGateway.Create(
            new KeyHealth { RequestsPerMinute = 100, TokensPerMinute = 100_000 },
            FrozenClock(DateTimeOffset.UtcNow),
            supervisor,
            audit,
            new BudgetCaps(PerAgentTokenCap: 1000, 0, 0, 0));

        // Accrue spend to the cap (as a settled request would).
        gateway.Ledger.Record("agent-1", "claude-3-5-haiku", 1000);

        // The next acquire is refused with a typed reason — and the agent is PAUSED, not killed.
        var ex = await Assert.ThrowsAsync<BudgetExhaustedException>(
            () => gateway.AcquireAsync("agent-1", 500, CancellationToken.None));
        Assert.Equal("agent-1", ex.AgentId);

        Assert.Contains("agent-1", supervisor.Paused);
        Assert.Empty(supervisor.Resumed);                       // never resumed → still paused
        Assert.Equal("BudgetExhausted", supervisor.LastState("agent-1"));

        // AuditProbe-equivalent: exactly one budget_exceeded event carrying the agent id.
        var events = audit.Read().Where(e => e.Type == "budget_exceeded").ToArray();
        Assert.Single(events);
        Assert.Equal("agent-1", events[0].Fields["agent_id"]);
    }

    [Fact]
    public void Snapshot_ReportsPerAgentSpend_AndTotalsMatchRows()
    {
        var gateway = AiGateway.Create(
            new KeyHealth { RequestsPerMinute = 100, TokensPerMinute = 100_000 },
            FrozenClock(DateTimeOffset.UtcNow));

        gateway.Ledger.Record("a", "gpt-4o", 100);
        gateway.Ledger.Record("a", "gpt-4o", 50);
        gateway.Ledger.Record("b", "gpt-4o-mini", 200);

        var snapshot = gateway.GetSnapshot();
        var a = snapshot.Agents.Single(x => x.AgentId == "a");
        var b = snapshot.Agents.Single(x => x.AgentId == "b");

        Assert.Equal(150, a.Tokens);
        Assert.Equal(200, b.Tokens);

        // Snapshot totals reconcile with the raw ledger rows.
        var rows = gateway.Ledger.AllRows();
        Assert.Equal(rows.Where(r => r.AgentId == "a").Sum(r => r.Tokens), a.Tokens);
        Assert.Equal(rows.Sum(r => r.UsdMicros), snapshot.Agents.Sum(x => x.UsdMicros));
    }

    [Fact]
    public async Task Snapshot_ReportsQueueDepth_ForWaitingAgent()
    {
        var clock = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bucket = new TokenBucket(1, 1000, () => clock);
        var gateway = new AiGateway(bucket, new BudgetLedger(new InMemorySpendStore(), () => clock));

        // Drain the single request permit, then leave one acquire queued.
        var first = await gateway.AcquireAsync("a", 500, CancellationToken.None);
        Assert.NotNull(first);
        using var cts = new CancellationTokenSource();
        var queued = gateway.AcquireAsync("a", 500, cts.Token);

        var snapshot = gateway.GetSnapshot();
        Assert.Equal(1, snapshot.TotalQueueDepth);
        Assert.Equal(1, snapshot.Agents.Single(x => x.AgentId == "a").QueueDepth);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }
}
