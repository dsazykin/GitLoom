using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Orchestrator;

/// <summary>
/// The P2-09 leader-reattach boot step. Runs <b>after</b> the P2-08 swarm (container) reconcile so the
/// reconcile order is containers → leaders → PTY reattach (contract §3.4): it lists the live containers
/// and hands them to <see cref="SessionLeader.Reattach"/>, which reattaches surviving PTY sessions and
/// reaps the rest toward Docker truth. Best-effort — a Docker hiccup must never block the daemon.
/// </summary>
public sealed class LeaderReattachTask : IBootTask
{
    private readonly SessionLeader _leader;
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> _listContainers;

    public LeaderReattachTask(
        SessionLeader leader,
        Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> listContainers)
    {
        _leader = leader ?? throw new ArgumentNullException(nameof(leader));
        _listContainers = listContainers ?? throw new ArgumentNullException(nameof(listContainers));
    }

    public string Name => "leader-reattach";

    public async Task RunAsync(CancellationToken ct)
    {
        var containers = await _listContainers(ct).ConfigureAwait(false);
        _leader.Reattach(containers);
    }
}
