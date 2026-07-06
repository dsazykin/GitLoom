using System;
using System.Collections.Generic;

namespace GitLoom.Core.Analytics;

/// <summary>
/// Commit activity bucketed by (weekday, hour) — the punch-card heatmap source. Pure and testable:
/// bucketing uses each commit's own UTC offset (the author's wall-clock at commit time), never
/// <c>ToLocalTime()</c>, so a scripted <see cref="DateTimeOffset"/> lands in the same cell on every
/// machine/CI (a punch card of "when people commit in their own timezone" is what we want anyway).
/// </summary>
public class PunchCardStats
{
    // 7 days of the week (0 = Sunday), 24 hours per day.
    public int[,] CommitsByDayHour { get; } = new int[7, 24];

    public int TotalCommits { get; private set; }

    /// <summary>Max commits in any single (weekday, hour) cell — the heatmap's upper bound.</summary>
    public int PeakCount { get; private set; }

    /// <summary>Builds a punch card from a commit-stat list (all commits count, merges included).</summary>
    public static PunchCardStats FromCommits(IEnumerable<CommitStat> commits)
    {
        var stats = new PunchCardStats();
        foreach (var c in commits) stats.AddCommit(c.When);
        return stats;
    }

    public void AddCommit(DateTimeOffset commitDate)
    {
        // Use the offset's wall-clock (deterministic), NOT ToLocalTime() (machine-zone dependent).
        int dayOfWeek = (int)commitDate.DayOfWeek;
        int hour = commitDate.Hour;

        int count = ++CommitsByDayHour[dayOfWeek, hour];
        if (count > PeakCount) PeakCount = count;
        TotalCommits++;
    }

    public IEnumerable<PunchCardDataPoint> GetDataPoints()
    {
        for (int d = 0; d < 7; d++)
        {
            for (int h = 0; h < 24; h++)
            {
                if (CommitsByDayHour[d, h] > 0)
                {
                    yield return new PunchCardDataPoint
                    {
                        DayOfWeek = d,
                        Hour = h,
                        CommitCount = CommitsByDayHour[d, h]
                    };
                }
            }
        }
    }
}

public struct PunchCardDataPoint
{
    public int DayOfWeek { get; set; }
    public int Hour { get; set; }
    public int CommitCount { get; set; }
}
