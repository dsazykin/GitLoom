using System;
using Mainguard.Agents.Agents;
using Mainguard.Git.Security;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-08 test contract #3 — backoff honors <c>Retry-After</c> on a virtual clock. A
/// <c>Retry-After: 5</c> resumes at ≈5 s; repeated 429s back off exponentially; the floor always wins.
/// </summary>
public class BackoffTests
{
    [Fact]
    public void RetryAfter_IsHonoredAsFloor()
    {
        // First attempt would otherwise back off ~1s; the Retry-After: 5 floor wins.
        var delay = GatewayBackoff.Compute(attempt: 1, retryAfter: TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(5), delay);
    }

    [Fact]
    public void Backoff_IsExponential_WhenNoRetryAfter()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), GatewayBackoff.Compute(1, null));
        Assert.Equal(TimeSpan.FromSeconds(2), GatewayBackoff.Compute(2, null));
        Assert.Equal(TimeSpan.FromSeconds(4), GatewayBackoff.Compute(3, null));
        Assert.Equal(TimeSpan.FromSeconds(8), GatewayBackoff.Compute(4, null));
    }

    [Fact]
    public void Backoff_IsCapped()
    {
        var delay = GatewayBackoff.Compute(attempt: 40, retryAfter: null);
        Assert.True(delay <= GatewayBackoff.MaxDelay);
    }

    [Fact]
    public void ExponentialWins_WhenGreaterThanRetryAfter()
    {
        // Attempt 5 → 16s exponential, which exceeds a 2s Retry-After.
        var delay = GatewayBackoff.Compute(attempt: 5, retryAfter: TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(16), delay);
    }

    [Fact]
    public void Gateway_Report429_SchedulesResumeAtRetryAfter_OnVirtualClock()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Func<DateTimeOffset> clock = () => now;
        var gateway = AiGateway.Create(new KeyHealth { RequestsPerMinute = 100, TokensPerMinute = 100_000 }, clock);

        gateway.Report429("agent-1", TimeSpan.FromSeconds(5));

        // Immediately after the 429, ~5s of backoff remain; 5s later the window has elapsed.
        Assert.Equal(5.0, gateway.RemainingBackoff("agent-1").TotalSeconds, precision: 1);
        now = now.AddSeconds(5);
        Assert.Equal(TimeSpan.Zero, gateway.RemainingBackoff("agent-1"));
    }
}
