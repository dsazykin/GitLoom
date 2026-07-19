using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;
using Mainguard.Server;
using Mainguard.Server.Runtime;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Audit fix #15: subscriber channels are bounded — a stalled client must not grow daemon memory
/// without limit. On overflow the subscription COMPLETES (never a silent delta drop, which would
/// desync the client forever); the client's normal reconnect then resyncs via a fresh snapshot.
/// </summary>
public sealed class AgentSessionStoreBackpressureTests
{
    [Fact]
    public void StalledSubscriber_OverflowsItsBuffer_StreamCompletes_InsteadOfGrowingForever()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        var reader = store.Subscribe(out var unsubscribe);

        // Never read while writing: fill the bounded buffer past capacity (the snapshot already
        // occupies one slot).
        for (var i = 0; i < AgentSessionStore.SubscriberBufferCapacity + 10; i++)
        {
            store.Spawn("kind");
        }

        // The buffer is finite and the stream ENDS: drain what was buffered, then the channel is
        // complete (the store dropped the subscriber on overflow) — later events never arrive.
        var drained = 0;
        while (reader.TryRead(out _))
        {
            drained++;
        }

        Assert.InRange(drained, 1, AgentSessionStore.SubscriberBufferCapacity);
        store.Spawn("late");
        Assert.False(reader.TryRead(out _), "a dropped subscriber must receive nothing further");
        Assert.True(reader.Completion.IsCompleted,
            "an overflowing subscription must be completed, ending the client stream");
        unsubscribe(); // still safe after the store already dropped the subscriber
    }

    [Fact]
    public async Task HealthySubscriber_StillReceivesSnapshotThenDeltas()
    {
        var store = new AgentSessionStore(new InMemoryAuditLog());
        var reader = store.Subscribe(out var unsubscribe);
        store.Spawn("worker");

        var snapshot = await reader.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal("snapshot", snapshot.Kind);
        var delta = await reader.ReadAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal("state", delta.Kind);

        unsubscribe();
    }
}

/// <summary>Audit hardening: an unparseable <c>--port</c> must fail loudly, not silently fall back
/// to the default port.</summary>
public sealed class DaemonOptionsParseTests
{
    [Fact]
    public void ValidPort_Parses()
    {
        Assert.Equal(6001, DaemonOptions.Parse(new[] { "--port", "6001" }).Port);
    }

    [Theory]
    [InlineData("--port", "not-a-number")]
    [InlineData("--port", "0")]
    [InlineData("--port", "70000")]
    [InlineData("--port")]
    public void InvalidPort_ThrowsInsteadOfSilentlyIgnoring(params string[] args)
    {
        Assert.Throws<ArgumentException>(() => DaemonOptions.Parse(args));
    }
}
