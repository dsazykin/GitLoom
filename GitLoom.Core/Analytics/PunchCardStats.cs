using System;
using System.Collections.Generic;

namespace GitLoom.Core.Analytics;

public class PunchCardStats
{
    // 7 days of the week, 24 hours per day
    public int[,] CommitsByDayHour { get; } = new int[7, 24];

    public int TotalCommits { get; private set; }

    public void AddCommit(DateTimeOffset commitDate)
    {
        var localTime = commitDate.ToLocalTime();
        int dayOfWeek = (int)localTime.DayOfWeek;
        int hour = localTime.Hour;

        CommitsByDayHour[dayOfWeek, hour]++;
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
