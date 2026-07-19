using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Models;

namespace GitLoom.Core.Agents;

/// <summary>Per-agent and per-day token + cost caps. A zero or negative cap means "unlimited".</summary>
/// <param name="PerAgentTokenCap">Max lifetime tokens for a single agent (0 = unlimited).</param>
/// <param name="PerAgentUsdMicrosCap">Max lifetime cost (USD micros) for a single agent (0 = unlimited).</param>
/// <param name="PerDayTokenCap">Max tokens across all agents in a UTC day (0 = unlimited).</param>
/// <param name="PerDayUsdMicrosCap">Max cost (USD micros) across all agents in a UTC day (0 = unlimited).</param>
public sealed record BudgetCaps(
    long PerAgentTokenCap,
    long PerAgentUsdMicrosCap,
    long PerDayTokenCap,
    long PerDayUsdMicrosCap)
{
    /// <summary>No caps — every request is admitted (the default until the user sets budgets).</summary>
    public static BudgetCaps Unlimited { get; } = new(0, 0, 0, 0);
}

/// <summary>The accumulated spend used by snapshots and the cost-per-merged-change join (P2-10).</summary>
public readonly record struct SpendTotals(long Tokens, long UsdMicros);

/// <summary>
/// The static per-model price table (documented in code). Prices are USD micro-dollars per 1M tokens
/// — a blended input/output figure adequate for budget accounting, not billing. Unknown models fall
/// back to a conservative default so an unlisted model still costs something.
/// </summary>
public static class ModelPriceTable
{
    // USD micros per 1,000,000 tokens (i.e. $3.00 → 3_000_000). Blended input+output.
    private static readonly IReadOnlyDictionary<string, long> Prices = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-3-5-haiku"] = 1_000_000,     // ~$1.00 / Mtok blended
        ["claude-3-5-sonnet"] = 6_000_000,    // ~$6.00 / Mtok blended
        ["claude-3-7-sonnet"] = 6_000_000,
        ["claude-3-opus"] = 30_000_000,       // ~$30.00 / Mtok blended
        ["gpt-4o"] = 5_000_000,
        ["gpt-4o-mini"] = 400_000,
        ["gpt-4-turbo"] = 20_000_000,
    };

    /// <summary>Conservative fallback for an unlisted model ($5.00 / Mtok).</summary>
    public const long DefaultUsdMicrosPerMillionTokens = 5_000_000;

    /// <summary>USD micro-dollars for <paramref name="tokens"/> of <paramref name="model"/> (prefix match).</summary>
    public static long CostMicros(string model, long tokens)
    {
        var rate = RateFor(model);
        // micros = tokens * rate / 1_000_000, computed in 128-bit to avoid overflow on large runs.
        return (long)((System.Numerics.BigInteger)tokens * rate / 1_000_000);
    }

    private static long RateFor(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return DefaultUsdMicrosPerMillionTokens;
        }

        if (Prices.TryGetValue(model, out var exact))
        {
            return exact;
        }

        // Longest-prefix match so "claude-3-5-sonnet-20241022" maps to "claude-3-5-sonnet".
        var match = Prices.Keys
            .Where(k => model.StartsWith(k, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(k => k.Length)
            .FirstOrDefault();
        return match is not null ? Prices[match] : DefaultUsdMicrosPerMillionTokens;
    }
}

/// <summary>The persistence seam for spend rows — SQLite in the daemon, in-memory in tests.</summary>
public interface ISpendStore
{
    /// <summary>Appends one settled spend row (assigns its <see cref="SpendRecord.Id"/>).</summary>
    void Append(SpendRecord record);

    /// <summary>All rows, insertion order.</summary>
    IReadOnlyList<SpendRecord> All();
}

/// <summary>
/// P2-08 budget ledger. Records settled spend (tokens + cost) per agent, enforces per-agent and
/// per-day caps, and raises <see cref="SpendRecorded"/> so the daemon can stream rows over
/// <c>GatewayService.StreamSpend</c>. Budget exhaustion is a <b>typed pause</b> signal — the caller
/// pauses the agent, it is never killed (rejection trigger). Rows carry <c>agentId</c> and
/// <see cref="GetSpendSince"/> exposes the cost-per-merged-change join for P2-10.
/// </summary>
public sealed class BudgetLedger
{
    private readonly ISpendStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private BudgetCaps _caps;

    public BudgetLedger(ISpendStore store, Func<DateTimeOffset> clock, BudgetCaps? caps = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _caps = caps ?? BudgetCaps.Unlimited;
    }

    /// <summary>Raised (outside the lock) for each appended row so the daemon can stream it live.</summary>
    public event Action<SpendRecord>? SpendRecorded;

    /// <summary>The current caps (get/set persisted by the gRPC budgets endpoints).</summary>
    public BudgetCaps Caps
    {
        get { lock (_gate) { return _caps; } }
        set { lock (_gate) { _caps = value ?? BudgetCaps.Unlimited; } }
    }

    /// <summary>
    /// True when <paramref name="agentId"/> is at/over any cap and must be paused. The
    /// <paramref name="reason"/> names the cap and its value (honest, user-facing). Checked before a
    /// request is forwarded, so an already-exhausted agent never issues more spend.
    /// </summary>
    public bool IsExhausted(string agentId, out string reason)
    {
        lock (_gate)
        {
            var caps = _caps;
            var agent = TotalsForAgentLocked(agentId);
            if (caps.PerAgentTokenCap > 0 && agent.Tokens >= caps.PerAgentTokenCap)
            {
                reason = $"Agent budget reached: {Tokens(agent.Tokens)} of {Tokens(caps.PerAgentTokenCap)} tokens used.";
                return true;
            }

            if (caps.PerAgentUsdMicrosCap > 0 && agent.UsdMicros >= caps.PerAgentUsdMicrosCap)
            {
                reason = $"Agent budget reached: {FormatUsd(agent.UsdMicros)} of {FormatUsd(caps.PerAgentUsdMicrosCap)} spent.";
                return true;
            }

            var day = TotalsForDayLocked(_clock().UtcDateTime.Date);
            if (caps.PerDayTokenCap > 0 && day.Tokens >= caps.PerDayTokenCap)
            {
                reason = $"Daily budget reached: {Tokens(day.Tokens)} of {Tokens(caps.PerDayTokenCap)} tokens used today.";
                return true;
            }

            if (caps.PerDayUsdMicrosCap > 0 && day.UsdMicros >= caps.PerDayUsdMicrosCap)
            {
                reason = $"Daily budget reached: {FormatUsd(day.UsdMicros)} of {FormatUsd(caps.PerDayUsdMicrosCap)} spent today.";
                return true;
            }

            reason = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Records actual settled spend for a request and returns the persisted row. Fires
    /// <see cref="SpendRecorded"/> so the daemon streams it. Recording is always allowed (the request
    /// already happened); the cap gate is <see cref="IsExhausted"/>, consulted before the next request.
    /// </summary>
    public SpendRecord Record(string agentId, string model, long tokens)
    {
        var record = new SpendRecord
        {
            AgentId = agentId,
            Model = model ?? string.Empty,
            Tokens = tokens,
            UsdMicros = ModelPriceTable.CostMicros(model ?? string.Empty, tokens),
            WhenUtc = _clock().UtcDateTime,
        };

        lock (_gate)
        {
            _store.Append(record);
        }

        SpendRecorded?.Invoke(record);
        return record;
    }

    /// <summary>Lifetime totals for one agent.</summary>
    public SpendTotals GetTotals(string agentId)
    {
        lock (_gate)
        {
            return TotalsForAgentLocked(agentId);
        }
    }

    /// <summary>
    /// The cost-per-merged-change join hook (P2-10): spend for <paramref name="agentId"/> at or after
    /// <paramref name="since"/>.
    /// </summary>
    public SpendTotals GetSpendSince(string agentId, DateTimeOffset since)
    {
        lock (_gate)
        {
            long tokens = 0, usd = 0;
            foreach (var r in _store.All())
            {
                if (string.Equals(r.AgentId, agentId, StringComparison.Ordinal) && r.WhenUtc >= since.UtcDateTime)
                {
                    tokens += r.Tokens;
                    usd += r.UsdMicros;
                }
            }

            return new SpendTotals(tokens, usd);
        }
    }

    /// <summary>All persisted rows (snapshot for <c>StreamSpend</c> replay and snapshot totals).</summary>
    public IReadOnlyList<SpendRecord> AllRows()
    {
        lock (_gate)
        {
            return _store.All();
        }
    }

    private SpendTotals TotalsForAgentLocked(string agentId)
    {
        long tokens = 0, usd = 0;
        foreach (var r in _store.All())
        {
            if (string.Equals(r.AgentId, agentId, StringComparison.Ordinal))
            {
                tokens += r.Tokens;
                usd += r.UsdMicros;
            }
        }

        return new SpendTotals(tokens, usd);
    }

    private SpendTotals TotalsForDayLocked(DateTime utcDay)
    {
        long tokens = 0, usd = 0;
        foreach (var r in _store.All())
        {
            if (r.WhenUtc.Date == utcDay)
            {
                tokens += r.Tokens;
                usd += r.UsdMicros;
            }
        }

        return new SpendTotals(tokens, usd);
    }

    private static string Tokens(long count) =>
        count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatUsd(long micros) =>
        (micros / 1_000_000.0).ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}

/// <summary>An in-memory <see cref="ISpendStore"/> for tests and the pre-persistence daemon path.</summary>
public sealed class InMemorySpendStore : ISpendStore
{
    private readonly object _gate = new();
    private readonly List<SpendRecord> _rows = new();
    private long _nextId;

    public void Append(SpendRecord record)
    {
        lock (_gate)
        {
            record.Id = ++_nextId;
            _rows.Add(record);
        }
    }

    public IReadOnlyList<SpendRecord> All()
    {
        lock (_gate)
        {
            return _rows.ToArray();
        }
    }
}
