using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using GitLoom.Core.Security;
using GitLoom.Server.Gateway;
using GitLoom.Server.Tests.Fixtures;
using Xunit;

namespace GitLoom.Server.Tests.Gateway;

/// <summary>
/// P2-08 invariant #1 (test contract #5), asserted end-to-end against the shared
/// <see cref="FakeModelEndpoint"/>: the fake returns a 429 (<c>Retry-After: 5</c>) then a 200, and the
/// agent-side caller observes <b>exactly one delayed 200</b> — never the 429. The worker's PTY input
/// is paused on the 429 and resumed on success; the agent is marked <c>RateLimited</c> then cleared.
/// </summary>
public class Fake429EndpointTests
{
    private sealed class RecordingSupervisor : IAgentSupervisor
    {
        public List<string> Paused { get; } = new();

        public List<string> Resumed { get; } = new();

        public List<string> States { get; } = new();

        public void PauseInput(string agentId) => Paused.Add(agentId);

        public void ResumeInput(string agentId) => Resumed.Add(agentId);

        public void MarkState(string agentId, string state, string? reason) => States.Add(state);
    }

    [Fact]
    public async Task Cli_NeverSees429_SeesDelayed200_PtyPausedThenResumed()
    {
        using var fake = new FakeModelEndpoint();
        fake.EnqueueRateLimited(retryAfterSeconds: 5)
            .EnqueueOk(body: "{\"ok\":true,\"usage\":{\"total_tokens\":321},\"model\":\"gpt-4o\"}");

        // Virtual clock: the injected delay advances it, so the ~5s backoff elapses instantly.
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Func<DateTimeOffset> clock = () => now;
        Task Delay(TimeSpan d, CancellationToken _)
        {
            now = now.Add(d);
            return Task.CompletedTask;
        }

        var supervisor = new RecordingSupervisor();
        var gateway = AiGateway.Create(
            new KeyHealth { RequestsPerMinute = 100, TokensPerMinute = 100_000 }, clock, supervisor);

        using var http = new HttpClient();
        var forwarder = new GatewayForwarder(gateway, http, Delay);

        using var request = new HttpRequestMessage(HttpMethod.Post, fake.BaseAddress)
        {
            Content = new StringContent("{\"prompt\":\"hi\"}"),
        };

        using var response = await forwarder.ForwardAsync("agent-1", request, estimatedTokens: 100, CancellationToken.None);

        // The caller sees exactly one 200 — the 429 was absorbed.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.ServedCount); // upstream served 429 then 200; the CLI issued one call.

        // PTY input paused on the 429 and resumed after the successful retry.
        Assert.Contains("agent-1", supervisor.Paused);
        Assert.Contains("agent-1", supervisor.Resumed);
        Assert.Contains("RateLimited", supervisor.States);
        Assert.Equal("Running", supervisor.States[^1]); // cleared back to Running last

        // The lease settled against the actual token usage from the response body.
        var snapshot = gateway.GetSnapshot();
        Assert.Equal(321, snapshot.Agents[0].Tokens);
    }

    [Fact]
    public async Task RepeatedRateLimits_StillResolveToSingle200()
    {
        using var fake = new FakeModelEndpoint();
        fake.EnqueueRateLimited(1).EnqueueRateLimited(2).EnqueueOk();

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Func<DateTimeOffset> clock = () => now;
        Task Delay(TimeSpan d, CancellationToken _) { now = now.Add(d); return Task.CompletedTask; }

        var gateway = AiGateway.Create(new KeyHealth { RequestsPerMinute = 100, TokensPerMinute = 100_000 }, clock);
        using var http = new HttpClient();
        var forwarder = new GatewayForwarder(gateway, http, Delay);

        using var request = new HttpRequestMessage(HttpMethod.Post, fake.BaseAddress);
        using var response = await forwarder.ForwardAsync("agent-2", request, 50, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, fake.ServedCount);
    }
}
