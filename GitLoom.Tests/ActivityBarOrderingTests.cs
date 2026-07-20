using System;
using System.Linq;
using GitLoom.App.ViewModels.Agents;
using Mainguard.Agents.Agents;
using Xunit;

namespace GitLoom.Tests;

// P2-13 test 2 (§5) / TI-P2-13.2: the rail agent list is LIFO — newest spawn first — and removing
// any agent leaves the relative order of the rest intact. Exercises the exact production ordering
// helper the rail uses (AgentListProjection), not a re-implementation.
public class ActivityBarOrderingTests
{
    private static AgentInfo Agent(string id, DateTimeOffset spawnedAt) =>
        new(id, id.ToUpperInvariant(), $"agent/{id}", AgentLifecycleState.Working, "working", spawnedAt);

    [Fact]
    public void ActivityBar_LifoOrdering()
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var a = Agent("a", t0);
        var b = Agent("b", t0.AddSeconds(1));
        var c = Agent("c", t0.AddSeconds(2));

        // Spawned A, then B, then C → the rail shows C, B, A.
        var order = AgentListProjection.LifoOrder(new[] { a, b, c }).Select(x => x.AgentId).ToArray();
        Assert.Equal(new[] { "c", "b", "a" }, order);

        // Removing the middle agent keeps the remaining order (C then A).
        var afterRemoval = AgentListProjection.LifoOrder(new[] { a, c }).Select(x => x.AgentId).ToArray();
        Assert.Equal(new[] { "c", "a" }, afterRemoval);
    }

    [Fact]
    public void LifoOrdering_IsStableRegardlessOfInputOrder()
    {
        var t0 = new DateTimeOffset(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);
        var a = Agent("a", t0);
        var b = Agent("b", t0.AddSeconds(1));
        var c = Agent("c", t0.AddSeconds(2));

        var fromScrambled = AgentListProjection.LifoOrder(new[] { b, a, c }).Select(x => x.AgentId);
        Assert.Equal(new[] { "c", "b", "a" }, fromScrambled);
    }
}
