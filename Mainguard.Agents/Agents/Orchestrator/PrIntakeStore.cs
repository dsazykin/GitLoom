using System;
using System.Collections.Generic;
using System.Linq;
using Mainguard.Git.Models;

using Mainguard.Git;
namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>
/// The P2-12 intake persistence seam (daemon SQLite; in-memory in tests). Holds two durable facts:
/// the set of <see cref="ExternalPrSource"/> subscriptions, and the last-seen head SHA per intake'd PR.
/// Follows the same daemon-store shape as the P2-10 queue/verification stores (in-memory + Db behind one
/// interface, a <c>Func&lt;AppDbContext&gt;</c> factory, a private lock).
/// </summary>
public interface IPrIntakeStore
{
    /// <summary>Persists a subscription. Returns false (and stores nothing new) when the exact
    /// <c>(host, owner, repo, filter)</c> is already subscribed — the idempotent path (edge row 3).</summary>
    bool AddSubscription(ExternalPrSource source);

    /// <summary>Every persisted subscription.</summary>
    IReadOnlyList<ExternalPrSource> Subscriptions();

    /// <summary>The last head SHA materialized for a PR, or null if it has never been materialized.</summary>
    string? GetSeenHead(string sourceKey, int prNumber);

    /// <summary>Records (upserts) the head SHA last materialized for a PR.</summary>
    void SetSeenHead(string sourceKey, int prNumber, string headSha);

    /// <summary>The PR numbers currently tracked for a source (drives closed-PR detection).</summary>
    IReadOnlyList<int> TrackedPrNumbers(string sourceKey);

    /// <summary>Stops tracking a PR (its entry closed/merged upstream and was cleaned up).</summary>
    void Untrack(string sourceKey, int prNumber);
}

/// <summary>An in-memory <see cref="IPrIntakeStore"/> for tests and the pre-persistence path.</summary>
public sealed class InMemoryPrIntakeStore : IPrIntakeStore
{
    private readonly object _gate = new();
    private readonly List<ExternalPrSource> _sources = new();
    private readonly Dictionary<(string SourceKey, int PrNumber), string> _heads = new();

    public bool AddSubscription(ExternalPrSource source)
    {
        lock (_gate)
        {
            if (_sources.Any(s => SameSource(s, source)))
            {
                return false;
            }

            _sources.Add(source);
            return true;
        }
    }

    public IReadOnlyList<ExternalPrSource> Subscriptions()
    {
        lock (_gate)
        {
            return _sources.ToList();
        }
    }

    public string? GetSeenHead(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            return _heads.TryGetValue((sourceKey, prNumber), out var sha) ? sha : null;
        }
    }

    public void SetSeenHead(string sourceKey, int prNumber, string headSha)
    {
        lock (_gate)
        {
            _heads[(sourceKey, prNumber)] = headSha;
        }
    }

    public IReadOnlyList<int> TrackedPrNumbers(string sourceKey)
    {
        lock (_gate)
        {
            return _heads.Keys.Where(k => k.SourceKey == sourceKey).Select(k => k.PrNumber).ToList();
        }
    }

    public void Untrack(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            _heads.Remove((sourceKey, prNumber));
        }
    }

    private static bool SameSource(ExternalPrSource a, ExternalPrSource b) =>
        string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Owner, b.Owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Repo, b.Repo, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.AuthorFilter ?? "", b.AuthorFilter ?? "", StringComparison.OrdinalIgnoreCase);
}

/// <summary>SQLite-backed <see cref="IPrIntakeStore"/> — durable subscriptions + seen heads (daemon DB).</summary>
public sealed class DbPrIntakeStore : IPrIntakeStore
{
    private readonly Func<AppDbContext> _contextFactory;
    private readonly object _gate = new();

    public DbPrIntakeStore(Func<AppDbContext> contextFactory)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
    }

    public bool AddSubscription(ExternalPrSource source)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var filter = source.AuthorFilter ?? "";
            var exists = db.PrIntakeSubscriptions.Any(s =>
                s.Host == source.Host && s.Owner == source.Owner && s.Repo == source.Repo
                && (s.AuthorFilter ?? "") == filter);
            if (exists)
            {
                return false;
            }

            db.PrIntakeSubscriptions.Add(new PrIntakeSubscriptionRow
            {
                Host = source.Host,
                Owner = source.Owner,
                Repo = source.Repo,
                AuthorFilter = source.AuthorFilter,
            });
            db.SaveChanges();
            return true;
        }
    }

    public IReadOnlyList<ExternalPrSource> Subscriptions()
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.PrIntakeSubscriptions
                .OrderBy(s => s.Id)
                .ToList()
                .Select(s => new ExternalPrSource(s.Host, s.Owner, s.Repo, s.AuthorFilter))
                .ToList();
        }
    }

    public string? GetSeenHead(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.PrIntakeHeads
                .Where(h => h.SourceKey == sourceKey && h.PrNumber == prNumber)
                .Select(h => h.SeenHeadSha)
                .FirstOrDefault();
        }
    }

    public void SetSeenHead(string sourceKey, int prNumber, string headSha)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.PrIntakeHeads.FirstOrDefault(h => h.SourceKey == sourceKey && h.PrNumber == prNumber);
            if (existing is null)
            {
                db.PrIntakeHeads.Add(new PrIntakeHeadRow
                {
                    SourceKey = sourceKey,
                    PrNumber = prNumber,
                    SeenHeadSha = headSha,
                });
            }
            else
            {
                existing.SeenHeadSha = headSha;
            }

            db.SaveChanges();
        }
    }

    public IReadOnlyList<int> TrackedPrNumbers(string sourceKey)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            return db.PrIntakeHeads.Where(h => h.SourceKey == sourceKey).Select(h => h.PrNumber).ToList();
        }
    }

    public void Untrack(string sourceKey, int prNumber)
    {
        lock (_gate)
        {
            using var db = _contextFactory();
            var existing = db.PrIntakeHeads.FirstOrDefault(h => h.SourceKey == sourceKey && h.PrNumber == prNumber);
            if (existing is null)
            {
                return;
            }

            db.PrIntakeHeads.Remove(existing);
            db.SaveChanges();
        }
    }
}
