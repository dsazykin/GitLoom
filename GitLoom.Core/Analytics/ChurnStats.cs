using System;
using System.Collections.Generic;
using System.Linq;

namespace GitLoom.Core.Analytics;

/// <summary>One weekly churn bucket: added / removed line totals for the week starting <see cref="WeekStart"/>.</summary>
/// <param name="WeekStart">The Monday (00:00) of the ISO-style week the commits fall in.</param>
public readonly record struct ChurnWeek(DateOnly WeekStart, long Added, long Removed)
{
    /// <summary>Added + removed — the week's total activity.</summary>
    public long Total => Added + Removed;
}

/// <summary>
/// Code-churn aggregation: added/removed line counts bucketed by week. Pure and testable — bucketing
/// on scripted <see cref="CommitStat.When"/>s yields exact, pinned totals. Merge commits are excluded
/// (they'd double-count their branch's work); binary files contribute 0 (the walk reports 0 lines for
/// them). Weeks between the first and last commit are zero-filled so the time axis is continuous.
/// </summary>
public sealed class ChurnStats
{
    public IReadOnlyList<ChurnWeek> Weeks { get; }

    public long TotalAdded { get; }
    public long TotalRemoved { get; }

    private ChurnStats(IReadOnlyList<ChurnWeek> weeks)
    {
        Weeks = weeks;
        TotalAdded = weeks.Sum(w => w.Added);
        TotalRemoved = weeks.Sum(w => w.Removed);
    }

    public static ChurnStats FromCommits(IEnumerable<CommitStat> commits)
    {
        // Non-merge commits only; a merge's diff-vs-first-parent restates its branch and would
        // double-count, so churn tracks the linear work.
        var contributing = commits.Where(c => !c.IsMerge).ToList();
        if (contributing.Count == 0) return new ChurnStats(Array.Empty<ChurnWeek>());

        var byWeek = new Dictionary<DateOnly, (long Added, long Removed)>();
        foreach (var c in contributing)
        {
            var week = WeekStartOf(c.When);
            byWeek.TryGetValue(week, out var acc);
            byWeek[week] = (acc.Added + c.LinesAdded, acc.Removed + c.LinesRemoved);
        }

        // Zero-fill every week from the earliest to the latest so the series has no gaps.
        var first = byWeek.Keys.Min();
        var last = byWeek.Keys.Max();
        var weeks = new List<ChurnWeek>();
        for (var w = first; w <= last; w = w.AddDays(7))
        {
            byWeek.TryGetValue(w, out var acc);
            weeks.Add(new ChurnWeek(w, acc.Added, acc.Removed));
        }

        return new ChurnStats(weeks);
    }

    /// <summary>The Monday of the week containing <paramref name="when"/>, on the commit's own offset.</summary>
    internal static DateOnly WeekStartOf(DateTimeOffset when)
    {
        var date = DateOnly.FromDateTime(when.DateTime);
        // DayOfWeek: Sunday = 0 … Saturday = 6. Offset back to Monday.
        int deltaToMonday = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-deltaToMonday);
    }
}
