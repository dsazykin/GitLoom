namespace Mainguard.Git.Models;

/// <summary>
/// One persisted external-PR-intake subscription (P2-12): a <c>(host, owner, repo, author-filter)</c>
/// tuple the daemon polls for bot-authored pull requests. Uniqueness is on all four fields so a
/// duplicate subscribe is idempotent (edge row 3). One row per subscription.
/// </summary>
public class PrIntakeSubscriptionRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The host (e.g. <c>github.com</c>).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>The repository owner / org.</summary>
    public string Owner { get; set; } = string.Empty;

    /// <summary>The repository name.</summary>
    public string Repo { get; set; } = string.Empty;

    /// <summary>An optional per-source author filter overriding the daemon's default bot list (null = use the default list).</summary>
    public string? AuthorFilter { get; set; }
}

/// <summary>
/// The last-seen head SHA for an intake'd PR (P2-12). Keyed by <c>SourceKey</c> (<c>host/owner/repo</c>)
/// and PR number, it is the "seen PR head SHAs" store the poll compares against: a new number or a moved
/// SHA drives (re-)materialization; the set of rows for a source is also the set of tracked PRs (a row
/// that no longer appears open upstream is a closed PR to clean up). One row per (source, PR number).
/// </summary>
public class PrIntakeHeadRow
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The source key (<c>host/owner/repo</c>) this PR belongs to.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>The upstream PR number.</summary>
    public int PrNumber { get; set; }

    /// <summary>The last head SHA materialized as <c>agent/pr-&lt;n&gt;</c>.</summary>
    public string SeenHeadSha { get; set; } = string.Empty;
}
