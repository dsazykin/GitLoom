using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Mainguard.Git.Audit;
using GitLoom.Protos.V1;
using GitLoom.Server.Tests.Fixtures;
using Grpc.Core;

namespace GitLoom.Server.Tests;

/// <summary>
/// TI-P2-00 acceptance (§A.4 / plan §6 row 7): the shared-fixture contracts — DaemonFixture
/// smoke, FakeModelEndpoint replay determinism, DualRepoFixture round-trip, AuditProbe.
/// (The ScriptedAgentHarness self-test lives in GitLoom.Tests, where the harness is built.)
/// </summary>
public sealed class FixtureAcceptanceTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public FixtureAcceptanceTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task DaemonFixture_Smoke_AuthedOk_WrongTokenDenied()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());

        var ok = await client.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());
        Assert.NotNull(ok);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.ListAgentsAsync(new ListAgentsRequest(), _daemon.WrongTokenHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task FakeModelEndpoint_ShouldReplayScriptedResponses_Deterministically()
    {
        using var endpoint = new FakeModelEndpoint()
            .EnqueueOk()
            .EnqueueRateLimited(retryAfterSeconds: 5)
            .EnqueueUnauthorized();

        using var http = new HttpClient();
        var first = await http.GetAsync(endpoint.BaseAddress);
        var second = await http.GetAsync(endpoint.BaseAddress);
        var third = await http.GetAsync(endpoint.BaseAddress);

        Assert.Equal(200, (int)first.StatusCode);
        Assert.Equal(429, (int)second.StatusCode);
        Assert.Equal(5, second.Headers.RetryAfter?.Delta?.TotalSeconds);
        Assert.Equal(401, (int)third.StatusCode);
        Assert.Equal(3, endpoint.ServedCount);
    }

    [Fact]
    public void DualRepoFixture_BareMirror_ShouldMatchWorkRepoRefs()
    {
        using var dual = new DualRepoFixture();

        // A fresh commit + push keeps the quarantine mirror byte-identical to the work heads.
        dual.Commit("feature.txt", "content\n", "add feature");
        PushHead(dual);

        var work = DualRepoFixture.CaptureRefState(dual.WorkRepoPath);
        var bare = DualRepoFixture.CaptureRefState(dual.BareMirrorPath);

        Assert.NotEmpty(bare);
        foreach (var (refName, sha) in bare)
        {
            Assert.True(work.TryGetValue(refName, out var workSha) && workSha == sha,
                $"mirror ref {refName}={sha} not byte-identical in the work repo.");
        }
    }

    [Fact]
    public void AuditProbe_ShouldAssertSequenceAndExactlyOne()
    {
        var log = new InMemoryAuditLog();
        log.Append(new AuditEvent("spawn", new Dictionary<string, string> { ["agent_id"] = "a1" }));
        log.Append(new AuditEvent("plan_approved", new Dictionary<string, string> { ["agent_id"] = "a1" }));
        log.Append(new AuditEvent("merge", new Dictionary<string, string> { ["agent_id"] = "a1" }));

        var probe = new AuditProbe(log);
        probe.AssertSequence("spawn", "plan_approved", "merge");
        probe.AssertExactlyOne("plan_approved", e => e.Fields["agent_id"] == "a1");
    }

    private static void PushHead(DualRepoFixture dual)
    {
        using var repo = new LibGit2Sharp.Repository(dual.WorkRepoPath);
        var remote = repo.Network.Remotes[DualRepoFixture.QuarantineRemote];
        repo.Network.Push(remote, repo.Head.CanonicalName + ":" + repo.Head.CanonicalName);
    }
}
