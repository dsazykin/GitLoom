using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>
/// Lists the live agent jails straight from Docker by the <c>gitloom.agent</c> label P2-07 sets — the
/// <b>sole source of truth</b> the P2-08 <see cref="SwarmReconciler"/> consumes (no PID/lock files).
/// Kept separate from <see cref="DockerSandboxEngine"/> so the reconciler depends only on a listing
/// function, not the whole engine.
/// </summary>
public static class DockerAgentLister
{
    /// <summary>Reads every container carrying the <c>gitloom.agent</c> label into reconciler state.</summary>
    public static async Task<IReadOnlyList<AgentContainerState>> ListAsync(IDockerClient docker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(docker);
        var containers = await docker.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["label"] = new Dictionary<string, bool> { ["gitloom.agent"] = true },
            },
        }, ct).ConfigureAwait(false);

        return containers.Select(c => new AgentContainerState(
            AgentId: Label(c, "gitloom.agent"),
            RepoHash: Label(c, "gitloom.repo"),
            ContainerId: c.ID,
            Running: string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private static string Label(ContainerListResponse container, string key) =>
        container.Labels is not null && container.Labels.TryGetValue(key, out var value) ? value : string.Empty;
}
