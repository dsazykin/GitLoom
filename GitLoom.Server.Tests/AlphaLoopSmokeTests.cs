using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.App.Services;
using GitLoom.Server.Tests.Fixtures;
using Xunit;

namespace GitLoom.Server.Tests;

/// <summary>
/// P2-47 integration proof #3 — the Alpha loop smoke, run through the REAL composition root, not mocks.
/// It stands up the real in-proc daemon (<see cref="DaemonFixture"/> = the whole <c>DaemonHost</c> graph,
/// including the P2-47 intake chain), drives it with the shipped <see cref="DaemonClient"/>, and lets the
/// shipped <see cref="DaemonBackedOrchestrator"/> — the exact adapter MainWindow runs on — project the
/// daemon's agents off the live event stream. This is the headless-runnable leg of "prove the loop"
/// (launch → connect → spawn → observe → stop).
///
/// <para><b>Residual (documented, not faked):</b> the real <i>sandboxed</i> spawn (Docker), the P2-10
/// verification run, the P2-11 review, and the human foreground merge need a Docker host + a GUI and are
/// covered by the manual runbook — <c>SpawnAgent</c> here creates a daemon session (the client/stream/stop
/// legs), not a container. No mock is involved anywhere in this test.</para>
/// </summary>
public sealed class AlphaLoopSmokeTests
{
    [Fact]
    public async Task SpawnThroughRealDaemon_IsProjectedBy_ShippedAdapter_ThenStopped()
    {
        using var daemon = new DaemonFixture();
        // The shipped DaemonClient, pointed at the real in-proc daemon channel + its session token.
        using var client = new DaemonClient(daemon.CreateChannel, () => daemon.Token);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 1) Spawn through the real AgentService RPC (real AgentGrpcService → AgentSessionStore).
        var agentId = await client.SpawnAgentAsync(
            repoHandle: "repo-smoke", taskPrompt: "do the thing", agentKind: "worker",
            modelApiKey: "unused-in-session-only-path", ct: cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(agentId));

        // 2) The client sees it over the real ListAgents RPC.
        var listed = await client.ListAgentsAsync(cts.Token);
        Assert.Contains(listed, a => a.AgentId == agentId);

        // 3) The SHIPPED adapter projects it off the real snapshot-then-deltas event stream. Started after
        //    the spawn so the subscription snapshot carries the agent (spawn is snapshot-visible).
        using var adapter = new DaemonBackedOrchestrator(client, ownsClient: false);
        adapter.Start();

        var projected = await WaitUntilAsync(
            () => adapter.ListAgents().Any(a => a.AgentId == agentId),
            timeout: TimeSpan.FromSeconds(10));
        Assert.True(projected, "the shipped DaemonBackedOrchestrator did not project the spawned agent off the live stream");

        // 4) Stop through the real StopAgent RPC.
        var stopped = await client.StopAgentAsync(agentId, cts.Token);
        Assert.True(stopped);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }
}
