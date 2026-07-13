using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using GitLoom.Core.Agents.Sandbox;

namespace GitLoom.Server.Tests.Fixtures;

/// <summary>
/// TI-P2-07 §A.4 infrastructure contract: spawns a real hardened agent container through the P2-07
/// engine (default-deny egress + hardened spec) for the <c>RequiresDocker</c> suite, and cleans up
/// every container/worktree it created. This is the substrate the egress / inspect / git-proxy /
/// memory-scrape tests stand on — hand-rolling around it is a review rejection.
///
/// <para>The agent base image ref comes from <c>GITLOOM_AGENT_IMAGE</c> (default
/// <c>gitloom-agent-base:latest</c>) — CI builds it from <c>images/gitloom-agent-base/</c>; the image
/// is never built at runtime (G-16).</para>
/// </summary>
public sealed class SandboxFixture : IAsyncDisposable
{
    private readonly List<string> _containerIds = new();
    private readonly List<string> _tempWorktrees = new();

    public IDockerClient Docker { get; }
    public DockerSandboxEngine Engine { get; }
    public EgressProxyConfigurator Egress { get; }
    public string ImageRef { get; }

    public SandboxFixture()
    {
        Docker = new DockerClientConfiguration().CreateClient();
        ImageRef = Environment.GetEnvironmentVariable("GITLOOM_AGENT_IMAGE") ?? "gitloom-agent-base:latest";
        Egress = new EgressProxyConfigurator(Docker, EgressAllowlist.WithDefaults(new GitLoom.Core.Audit.InMemoryAuditLog()));
        Engine = new DockerSandboxEngine(Docker, new SandboxEngineOptions(Egress.NetworkName, Egress.ProxyUrl));
    }

    /// <summary>Ensures the default-deny network + proxy exist before spawning agents.</summary>
    public Task EnsureEgressReadyAsync(CancellationToken ct = default) => Egress.EnsureReadyAsync(ct);

    /// <summary>Spawns a hardened agent jail on an ext4 (temp) worktree; tracks it for cleanup.</summary>
    public async Task<SandboxHandle> SpawnAsync(
        string agentId = "agent-1", int agentUid = 1000, int supervisorUid = 1001, CancellationToken ct = default)
    {
        var worktree = NewTempWorktree();
        var secrets = new SandboxSecrets(
            new Dictionary<string, string> { ["ANTHROPIC_API_KEY"] = "sk-test-not-a-real-key" },
            OobKey: RandomKey());

        var handle = await Engine.SpawnAsync(new SandboxSpawnRequest(
            RepoHash: "sandboxfixture" + Guid.NewGuid().ToString("N")[..8],
            AgentId: agentId,
            WorktreePath: worktree,
            ImageRef: ImageRef,
            Limits: new SandboxLimits(1L * 1024 * 1024 * 1024, 256),
            Secrets: secrets,
            AgentUid: agentUid,
            SupervisorUid: supervisorUid), ct).ConfigureAwait(false);

        _containerIds.Add(handle.ContainerId);
        return handle;
    }

    /// <summary>Runs a command in a live sandbox and returns exit + output.</summary>
    public Task<SandboxExecResult> ExecAsync(string containerId, params string[] command)
        => Engine.ExecAsync(containerId, command);

    /// <summary>Inspects a spawned container (mounts, host config, state).</summary>
    public Task<ContainerInspectResponse> InspectAsync(string containerId, CancellationToken ct = default)
        => Docker.Containers.InspectContainerAsync(containerId, ct);

    private string NewTempWorktree()
    {
        // A real ext4 path on the Linux CI leg (/tmp) — never /mnt/c or a UNC (G-11).
        var path = Path.Combine(Path.GetTempPath(), "gitloom-sbx-wt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        _tempWorktrees.Add(path);
        return path;
    }

    private static byte[] RandomKey()
    {
        var key = new byte[32];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        return key;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _containerIds)
        {
            try { await Engine.RemoveAsync(id); }
            catch { /* never fail a test from cleanup */ }
        }

        foreach (var dir in _tempWorktrees)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }

        Docker.Dispose();
    }
}
