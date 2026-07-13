using System;
using GitLoom.Core.Agents;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-08 test contract #6 — admission control. A fake <c>/proc/meminfo</c> sampler at 86% rejects a
/// spawn with the honest ceiling text; below the threshold it admits. Existing agents are untouched.
/// </summary>
public class AdmissionControllerTests
{
    private const long SixteenGbKb = 16L * 1024 * 1024;

    private static MemorySample At(double usedFraction) =>
        new(SixteenGbKb, (long)(SixteenGbKb * (1.0 - usedFraction)));

    [Fact]
    public void CanSpawn_RejectsAboveThreshold_WithHonestReason()
    {
        var controller = new AdmissionController(
            sampler: () => At(0.86),
            runningAgentCount: () => 3,
            clock: () => DateTimeOffset.UtcNow);

        Assert.False(controller.CanSpawn(out var reason));
        Assert.Contains("Running 3 agents", reason);
        Assert.Contains("16 GB", reason);
        Assert.Contains("4–6", reason);      // the honest comfort band for 16 GB
        Assert.Contains("free memory or stop an agent", reason);
    }

    [Fact]
    public void CanSpawn_AdmitsBelowThreshold()
    {
        var controller = new AdmissionController(
            sampler: () => At(0.50),
            runningAgentCount: () => 2,
            clock: () => DateTimeOffset.UtcNow);

        Assert.True(controller.CanSpawn(out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void Sample_IsCachedWithinTtl()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var used = 0.50;
        var samples = 0;

        var controller = new AdmissionController(
            sampler: () => { samples++; return At(used); },
            runningAgentCount: () => 0,
            clock: () => now,
            cacheTtl: TimeSpan.FromSeconds(5));

        Assert.True(controller.CanSpawn(out _));
        used = 0.99;                      // memory spikes, but within the cache window
        Assert.True(controller.CanSpawn(out _)); // still the cached (admitting) reading
        Assert.Equal(1, samples);

        now = now.AddSeconds(6);          // cache expires → re-sampled → now blocks
        Assert.False(controller.CanSpawn(out _));
        Assert.Equal(2, samples);
    }

    [Fact]
    public void CanSpawn_UnknownMemory_Admits()
    {
        // Windows dev box with no /proc/meminfo → sample total 0 → used fraction 0 → admits.
        var controller = new AdmissionController(sampler: () => new MemorySample(0, 0));
        Assert.True(controller.CanSpawn(out _));
    }
}
