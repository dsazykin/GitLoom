using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Mainguard.Agents.Daemon;
using Mainguard.Agents.UI.Services;
using Mainguard.App.Shell.Services;
using Mainguard.Protos.V1;

namespace Mainguard.Tests;

/// <summary>
/// Client-side thin twin of the reconnect stream lifecycle: against a dead endpoint the
/// client degrades (retrying with backoff, no storm) and settles Down when cancelled,
/// yielding no events. The successful resume-after-restart twin runs in Mainguard.Server.Tests.
/// </summary>
public sealed class DaemonStreamTests
{
    [Fact]
    public async Task StreamAgentEvents_DeadEndpoint_ShouldDegrade_ThenDownOnCancel_WithNoEvents()
    {
        var deadPort = FreePort();
        var backoff = new BackoffPolicy(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(50), new Random(11));
        using var client = new DaemonClient(
            () => GrpcChannel.ForAddress($"http://127.0.0.1:{deadPort}"),
            () => "token",
            backoff);

        var degraded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<ConnectionState>();
        client.ConnectionStateChanged += s =>
        {
            lock (states)
            {
                states.Add(s);
            }

            if (s == ConnectionState.Degraded)
            {
                degraded.TrySetResult();
            }
        };

        var events = 0;
        using var cts = new CancellationTokenSource();
        var pump = Task.Run(async () =>
        {
            await foreach (var _ in client.StreamAgentEventsAsync(cts.Token))
            {
                Interlocked.Increment(ref events);
            }
        });

        await degraded.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cts.Cancel();
        await pump.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, events);
        List<ConnectionState> snapshot;
        lock (states)
        {
            snapshot = new List<ConnectionState>(states);
        }

        Assert.Contains(ConnectionState.Degraded, snapshot);
        Assert.Equal(ConnectionState.Down, snapshot[^1]);
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
