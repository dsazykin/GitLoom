using System;
using System.Linq;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-26 (pure) — pins every branch of the checks roll-up so the badge can never drift. Covers the
/// check-run status/conclusion table, the legacy commit-status table, the Overall reduction (all-pass,
/// any-fail, any-pending, neutral-only, mixed, empty), and the count/rollup builder.
/// </summary>
public class CheckStateMapperTests
{
    // ---- Check-run status + conclusion → CheckState -------------------------------------------

    [Theory]
    [InlineData("queued", null, CheckState.Pending)]
    [InlineData("in_progress", null, CheckState.Pending)]
    [InlineData("waiting", null, CheckState.Pending)]
    [InlineData("requested", null, CheckState.Pending)]
    [InlineData("pending", null, CheckState.Pending)]
    [InlineData("", null, CheckState.Pending)]
    // A not-yet-completed run is Pending even if a stale conclusion is present.
    [InlineData("in_progress", "success", CheckState.Pending)]
    // Completed → decided by conclusion.
    [InlineData("completed", "success", CheckState.Success)]
    [InlineData("completed", "failure", CheckState.Failure)]
    [InlineData("completed", "timed_out", CheckState.Failure)]
    [InlineData("completed", "cancelled", CheckState.Failure)]
    [InlineData("completed", "action_required", CheckState.Failure)]
    [InlineData("completed", "startup_failure", CheckState.Failure)]
    [InlineData("completed", "stale", CheckState.Failure)]
    [InlineData("completed", "neutral", CheckState.Neutral)]
    [InlineData("completed", "skipped", CheckState.Neutral)]
    [InlineData("completed", null, CheckState.Neutral)]
    [InlineData("completed", "something_new", CheckState.Neutral)]
    // Case/whitespace-insensitive.
    [InlineData("COMPLETED", "SUCCESS", CheckState.Success)]
    [InlineData(" completed ", " failure ", CheckState.Failure)]
    public void FromCheckRun_IsPinned(string status, string? conclusion, CheckState expected)
        => Assert.Equal(expected, CheckStateMapper.FromCheckRun(status, conclusion));

    // ---- Legacy combined-status state → CheckState --------------------------------------------

    [Theory]
    [InlineData("success", CheckState.Success)]
    [InlineData("failure", CheckState.Failure)]
    [InlineData("error", CheckState.Failure)]
    [InlineData("pending", CheckState.Pending)]
    [InlineData("", CheckState.Pending)]
    [InlineData("weird", CheckState.Pending)]
    [InlineData("SUCCESS", CheckState.Success)]
    public void FromLegacyStatus_IsPinned(string state, CheckState expected)
        => Assert.Equal(expected, CheckStateMapper.FromLegacyStatus(state));

    // ---- Overall roll-up ----------------------------------------------------------------------

    [Fact]
    public void Overall_Empty_IsSuccess() // value only; HasAny hides the badge for a truly empty commit
        => Assert.Equal(CheckState.Success, CheckStateMapper.Overall(Array.Empty<CheckState>()));

    [Fact]
    public void Overall_AllSuccess_IsSuccess()
        => Assert.Equal(CheckState.Success, CheckStateMapper.Overall(new[] { CheckState.Success, CheckState.Success }));

    [Fact]
    public void Overall_AnyFailure_Dominates()
        => Assert.Equal(CheckState.Failure, CheckStateMapper.Overall(
            new[] { CheckState.Success, CheckState.Pending, CheckState.Failure, CheckState.Neutral }));

    [Fact]
    public void Overall_PendingOverSuccess_WhenNoFailure()
        => Assert.Equal(CheckState.Pending, CheckStateMapper.Overall(
            new[] { CheckState.Success, CheckState.Pending, CheckState.Neutral }));

    [Fact]
    public void Overall_NeutralOnly_IsSuccess() // neutral is ignored → falls through to Success
        => Assert.Equal(CheckState.Success, CheckStateMapper.Overall(new[] { CheckState.Neutral, CheckState.Neutral }));

    [Fact]
    public void Overall_FailureBeatsPending()
        => Assert.Equal(CheckState.Failure, CheckStateMapper.Overall(new[] { CheckState.Pending, CheckState.Failure }));

    // ---- Rollup (counts + overall + HasAny) ---------------------------------------------------

    private static CheckRunItem Run(CheckState s, long id = 1) => new() { Id = id, Name = "c" + id, State = s };

    [Fact]
    public void Rollup_CountsAndOverall_NeutralNotCounted()
    {
        var runs = new[]
        {
            Run(CheckState.Success, 1), Run(CheckState.Success, 2),
            Run(CheckState.Failure, 3),
            Run(CheckState.Pending, 4),
            Run(CheckState.Neutral, 5),
        };
        var checks = CheckStateMapper.Rollup("abc123", runs);

        Assert.Equal("abc123", checks.Sha);
        Assert.Equal(CheckState.Failure, checks.Overall);
        Assert.Equal(2, checks.Passed);
        Assert.Equal(1, checks.Failed);
        Assert.Equal(1, checks.Pending);
        Assert.True(checks.HasAny);
        Assert.Equal(5, checks.Runs.Count);
    }

    [Fact]
    public void Rollup_Empty_HasAnyFalse()
    {
        var checks = CheckStateMapper.Rollup("deadbeef", Array.Empty<CheckRunItem>());
        Assert.False(checks.HasAny);
        Assert.Equal(0, checks.Passed);
        Assert.Equal(CheckState.Success, checks.Overall);
    }

    [Fact]
    public void Rollup_NeutralOnly_IsSuccess_AndHasAny()
    {
        var checks = CheckStateMapper.Rollup("sha", new[] { Run(CheckState.Neutral, 1) });
        Assert.True(checks.HasAny);
        Assert.Equal(CheckState.Success, checks.Overall);
        Assert.Equal(0, checks.Passed);
    }
}
