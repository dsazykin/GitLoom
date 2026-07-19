using System;
using Mainguard.Agents.UI.Services;
using Mainguard.App.Shell.Services;

namespace Mainguard.Tests;

/// <summary>
/// Client-side thin twin of TI-P2-02 §4/§5 (the full restart resume runs in
/// Mainguard.Server.Tests against a real host). Here: the pure backoff policy — jittered,
/// capped — that governs reconnect.
/// </summary>
public sealed class DaemonClientReconnectTests
{
    [Fact]
    public void Backoff_ShouldNeverExceedCap_AndStayNonNegative()
    {
        var policy = new BackoffPolicy(TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(30), new Random(7));

        for (var attempt = 0; attempt < 200; attempt++)
        {
            var delay = policy.Delay(attempt);
            Assert.True(delay >= TimeSpan.Zero, $"attempt {attempt} negative: {delay}");
            Assert.True(delay <= policy.Cap, $"attempt {attempt} exceeded cap: {delay} > {policy.Cap}");
        }
    }

    [Fact]
    public void Backoff_EarlyAttempts_ShouldStayUnderExponentialCeiling()
    {
        // Full-jitter: delay(attempt) <= base * 2^attempt (and <= cap). Guards the growth curve.
        var baseDelay = TimeSpan.FromMilliseconds(100);
        var policy = new BackoffPolicy(baseDelay, TimeSpan.FromSeconds(30), new Random(3));

        for (var attempt = 0; attempt < 6; attempt++)
        {
            var ceiling = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt));
            Assert.True(policy.Delay(attempt) <= ceiling);
        }
    }
}
