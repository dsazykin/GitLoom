using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git;
using Mainguard.Git.Models;
namespace Mainguard.Agents.Agents;

/// <summary>
/// SQLite-backed <see cref="ISpendStore"/> (P2-08 spend ledger persistence). Each op opens a
/// short-lived <see cref="AppDbContext"/> from the injected factory and disposes it — the same
/// handle-discipline ethos the git layer uses (no long-lived context). Spend rows survive a daemon
/// reboot so the cost-per-merged-change join is durable.
/// </summary>
public sealed class DbSpendStore : ISpendStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    public DbSpendStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public void Append(SpendRecord record)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            db.SpendRecords.Add(record);
            db.SaveChanges();
        }
    }

    public IReadOnlyList<SpendRecord> All()
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.SpendRecords.OrderBy(r => r.Id).ToList();
        }
    }
}

/// <summary>SQLite-backed <see cref="IExpectedAgentStore"/> (the reconciler's expected-agents table).</summary>
public sealed class DbExpectedAgentStore : IExpectedAgentStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    public DbExpectedAgentStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public IReadOnlyList<ExpectedAgent> All()
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.ExpectedAgents.OrderBy(a => a.Id).ToList();
        }
    }

    public void Upsert(string repoHash, string agentId, string disposition)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.ExpectedAgents.FirstOrDefault(a => a.RepoHash == repoHash && a.AgentId == agentId);
            if (existing is null)
            {
                db.ExpectedAgents.Add(new ExpectedAgent
                {
                    RepoHash = repoHash,
                    AgentId = agentId,
                    Disposition = disposition,
                });
            }
            else
            {
                existing.Disposition = disposition;
                existing.DisposalReason = null;
            }

            db.SaveChanges();
        }
    }

    public void MarkDead(string repoHash, string agentId, string reason)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.ExpectedAgents.FirstOrDefault(a => a.RepoHash == repoHash && a.AgentId == agentId);
            if (existing is null)
            {
                db.ExpectedAgents.Add(new ExpectedAgent
                {
                    RepoHash = repoHash,
                    AgentId = agentId,
                    Disposition = "Dead",
                    DisposalReason = reason,
                });
            }
            else
            {
                existing.Disposition = "Dead";
                existing.DisposalReason = reason;
            }

            db.SaveChanges();
        }
    }
}

/// <summary>The persistence seam for the single gateway budget row.</summary>
public interface IBudgetStore
{
    /// <summary>Reads the persisted budget (all-zero = unlimited when unset).</summary>
    GatewayBudget Get();

    /// <summary>Upserts the budget (per-agent + per-day caps) and returns the stored value.</summary>
    GatewayBudget Set(long usdMicrosCap, long tokenCap, long usdMicrosCapPerDay, long tokenCapPerDay);
}

/// <summary>In-memory <see cref="IBudgetStore"/> — the fallback when the daemon DB is unavailable.</summary>
public sealed class InMemoryBudgetStore : IBudgetStore
{
    private readonly object _gate = new();
    private GatewayBudget _row = new();

    public GatewayBudget Get()
    {
        lock (_gate)
        {
            return new GatewayBudget
            {
                Id = _row.Id,
                UsdMicrosCap = _row.UsdMicrosCap,
                TokenCap = _row.TokenCap,
                UsdMicrosCapPerDay = _row.UsdMicrosCapPerDay,
                TokenCapPerDay = _row.TokenCapPerDay,
            };
        }
    }

    public GatewayBudget Set(long usdMicrosCap, long tokenCap, long usdMicrosCapPerDay, long tokenCapPerDay)
    {
        lock (_gate)
        {
            _row = new GatewayBudget
            {
                Id = 1,
                UsdMicrosCap = usdMicrosCap,
                TokenCap = tokenCap,
                UsdMicrosCapPerDay = usdMicrosCapPerDay,
                TokenCapPerDay = tokenCapPerDay,
            };
            return Get();
        }
    }
}

/// <summary>SQLite-backed persistence for the single gateway budget row (get/set are durable).</summary>
public sealed class DbBudgetStore : IBudgetStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    public DbBudgetStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    /// <summary>Reads the persisted budget (all-zero = unlimited when unset).</summary>
    public GatewayBudget Get()
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.GatewayBudgets.FirstOrDefault() ?? new GatewayBudget();
        }
    }

    /// <summary>Upserts the single budget row and returns the stored value.</summary>
    public GatewayBudget Set(long usdMicrosCap, long tokenCap, long usdMicrosCapPerDay, long tokenCapPerDay)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var row = db.GatewayBudgets.FirstOrDefault();
            if (row is null)
            {
                row = new GatewayBudget
                {
                    Id = 1,
                    UsdMicrosCap = usdMicrosCap,
                    TokenCap = tokenCap,
                    UsdMicrosCapPerDay = usdMicrosCapPerDay,
                    TokenCapPerDay = tokenCapPerDay,
                };
                db.GatewayBudgets.Add(row);
            }
            else
            {
                row.UsdMicrosCap = usdMicrosCap;
                row.TokenCap = tokenCap;
                row.UsdMicrosCapPerDay = usdMicrosCapPerDay;
                row.TokenCapPerDay = tokenCapPerDay;
            }

            db.SaveChanges();
            return row;
        }
    }
}
