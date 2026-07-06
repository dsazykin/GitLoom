using System;
using System.Collections.Generic;
using System.Linq;

namespace GitLoom.Core.Analytics;

/// <summary>One contributor's rolled-up activity across the analyzed history.</summary>
/// <param name="Name">A display name for the identity (the most recent author name seen).</param>
/// <param name="Email">The identity key (case-insensitive email).</param>
/// <param name="Commits">Number of commits authored (merges included — they are still authored work).</param>
/// <param name="LinesAdded">Added lines across the contributor's non-merge commits.</param>
/// <param name="LinesRemoved">Removed lines across the contributor's non-merge commits.</param>
public readonly record struct ContributorStat(
    string Name, string Email, int Commits, long LinesAdded, long LinesRemoved);

/// <summary>
/// Contributor breakdown: commits (and churn) per author identity. Pure and testable. Identities are
/// keyed by lower-cased email so "Same Person &lt;a@b&gt;" under two display names merges; ordered by
/// commit count descending, then name, for a stable ranked bar chart.
/// </summary>
public static class ContributorStats
{
    public static IReadOnlyList<ContributorStat> FromCommits(IEnumerable<CommitStat> commits)
    {
        return commits
            .GroupBy(c => (c.AuthorEmail ?? string.Empty).Trim().ToLowerInvariant())
            .Select(g =>
            {
                var newest = g.OrderByDescending(c => c.When).First();
                return new ContributorStat(
                    Name: string.IsNullOrWhiteSpace(newest.AuthorName) ? newest.AuthorEmail : newest.AuthorName,
                    Email: g.Key,
                    Commits: g.Count(),
                    LinesAdded: g.Where(c => !c.IsMerge).Sum(c => c.LinesAdded),
                    LinesRemoved: g.Where(c => !c.IsMerge).Sum(c => c.LinesRemoved));
            })
            .OrderByDescending(s => s.Commits)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
