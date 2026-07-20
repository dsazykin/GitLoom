using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-P2-09 tests 1 & 2 (pure, fakes): the cooperative-yield handshake. The ready path completes
/// without a pause and asserts the request-before-ready ordering; the timeout path invokes
/// <c>docker pause</c> before returning the token, and the token's resume unpauses.
/// </summary>
public sealed class YieldProtocolTests
{
    [Fact]
    public async Task Yield_ReadyPath_RoundTrip_NoPause()
    {
        var channel = new RecordingChannel(readyAnswer: true);
        var sandbox = new RecordingSandbox();
        var protocol = new YieldProtocol(_ => channel, sandbox, _ => "container-1",
            defaultTimeout: TimeSpan.FromMilliseconds(50));

        using var token = await protocol.RequestYieldAsync("a1");

        Assert.Equal(YieldOutcome.ByReady, token.Outcome);
        Assert.True(token.IsActive);
        // Ordering: the request marker was sent, then the ready ack was awaited.
        Assert.Equal(new[] { YieldProtocol.UpdateRequested }, channel.Sent);
        Assert.True(channel.RequestedBeforeWait);
        // No pause on the cooperative path.
        Assert.Equal(0, sandbox.PauseCount);
    }

    [Fact]
    public async Task Yield_Timeout_PausePath_ThenResumeUnpauses()
    {
        var channel = new RecordingChannel(readyAnswer: false);
        var sandbox = new RecordingSandbox();
        var protocol = new YieldProtocol(_ => channel, sandbox, _ => "container-1",
            defaultTimeout: TimeSpan.FromMilliseconds(10));

        var token = await protocol.RequestYieldAsync("a1");

        // docker pause was invoked before the token (and thus before any mutation) is handed back.
        Assert.Equal(YieldOutcome.ByPause, token.Outcome);
        Assert.Equal(1, sandbox.PauseCount);
        Assert.Equal("container-1", sandbox.LastPaused);
        Assert.Equal(0, sandbox.UnpauseCount);

        token.Resume();

        Assert.False(token.IsActive);
        Assert.Equal(1, sandbox.UnpauseCount);

        // Resume is idempotent.
        token.Resume();
        Assert.Equal(1, sandbox.UnpauseCount);
    }

    [Fact]
    public async Task Yield_Timeout_NoLiveContainer_Throws()
    {
        var channel = new RecordingChannel(readyAnswer: false);
        var protocol = new YieldProtocol(_ => channel, new RecordingSandbox(), _ => null,
            defaultTimeout: TimeSpan.FromMilliseconds(10));

        await Assert.ThrowsAsync<InvalidOperationException>(() => protocol.RequestYieldAsync("a1"));
    }

    private sealed class RecordingChannel : IAgentControlChannel
    {
        private readonly bool _readyAnswer;
        private bool _sent;

        public RecordingChannel(bool readyAnswer) => _readyAnswer = readyAnswer;

        public List<string> Sent { get; } = new();

        public bool RequestedBeforeWait { get; private set; }

        public Task SendAsync(string marker, CancellationToken ct = default)
        {
            Sent.Add(marker);
            _sent = true;
            return Task.CompletedTask;
        }

        public Task<bool> WaitForAsync(string marker, TimeSpan timeout, CancellationToken ct = default)
        {
            RequestedBeforeWait = _sent;
            return Task.FromResult(_readyAnswer);
        }
    }

    private sealed class RecordingSandbox : ISandboxEngine
    {
        public int PauseCount { get; private set; }

        public int UnpauseCount { get; private set; }

        public string? LastPaused { get; private set; }

        public Task PauseAsync(string containerId, CancellationToken ct = default)
        {
            PauseCount++;
            LastPaused = containerId;
            return Task.CompletedTask;
        }

        public Task UnpauseAsync(string containerId, CancellationToken ct = default)
        {
            UnpauseCount++;
            return Task.CompletedTask;
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
