using System;

namespace Mainguard.Git.Analytics;

/// <summary>
/// One commit reduced to the numbers the pure analytics aggregators need
/// (<see cref="PunchCardStats"/>, <see cref="ChurnStats"/>, <see cref="ContributorStats"/>).
/// Produced by a single history walk in <see cref="RepositoryAnalyzer"/> and consumed by the
/// aggregators, which keeps every bucketing decision unit-testable with pinned numbers — no repo/IO.
/// </summary>
/// <param name="When">Commit timestamp; punch-card/churn bucket on the commit's own UTC offset
/// (wall-clock at the author's offset), never the machine's local zone, so buckets are deterministic.</param>
/// <param name="AuthorName">Author display name.</param>
/// <param name="AuthorEmail">Author email (identity key for contributor aggregation).</param>
/// <param name="LinesAdded">Added lines vs the first parent (0 for merges and binary files).</param>
/// <param name="LinesRemoved">Removed lines vs the first parent (0 for merges and binary files).</param>
/// <param name="ParentCount">Parent count — 0 root, 1 normal, ≥2 merge (merges excluded from churn).</param>
public readonly record struct CommitStat(
    DateTimeOffset When,
    string AuthorName,
    string AuthorEmail,
    long LinesAdded,
    long LinesRemoved,
    int ParentCount)
{
    /// <summary>A merge commit (more than one parent) — excluded from churn to avoid double-counting.</summary>
    public bool IsMerge => ParentCount > 1;
}
