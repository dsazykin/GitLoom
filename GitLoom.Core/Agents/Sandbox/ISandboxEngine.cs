using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Sandbox;

/// <summary>The two secrets delivered to a sandbox on spawn — never through <c>Env</c>/argv/disk.</summary>
/// <param name="AgentEnv">The P2-01 credential env-file entries, written to the agent-owned 0400 tmpfs.</param>
/// <param name="OobKey">The OOB session HMAC key <c>K</c>, written to the supervisor-owned 0400 tmpfs.</param>
public sealed record SandboxSecrets(IReadOnlyDictionary<string, string> AgentEnv, byte[] OobKey);

/// <summary>The request to spawn (or re-start) one agent's hardened jail.</summary>
public sealed record SandboxSpawnRequest(
    string RepoHash,
    string AgentId,
    string WorktreePath,
    string ImageRef,
    SandboxLimits Limits,
    SandboxSecrets Secrets,
    int AgentUid,
    int SupervisorUid);

/// <summary>A running sandbox handle. <see cref="Reused"/> is true when a stopped persistent jail was re-started rather than recreated.</summary>
public sealed record SandboxHandle(string ContainerId, bool Reused);

/// <summary>The outcome of an in-sandbox exec (e.g. <c>devbox add</c>).</summary>
public sealed record SandboxExecResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// The engine-agnostic sandbox lifecycle seam (P2-07). Deliberately carries <b>no</b> Docker.DotNet
/// types in its signature so an optional future <c>SbxSandboxEngine</c> (microVM) can implement it
/// without sbx becoming a hard dependency. The Docker implementation is
/// <see cref="DockerSandboxEngine"/>. Docker is the sole source of truth for liveness — there are no
/// PID/lock files.
/// </summary>
public interface ISandboxEngine
{
    /// <summary>Create-or-start the persistent jail keyed by repo hash + agent id (a stopped container is <c>docker start</c>ed; a base-image upgrade recreates).</summary>
    Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default);

    /// <summary>Run a command inside a live sandbox (e.g. <c>devbox add jq</c>) and return its exit + output.</summary>
    Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default);

    /// <summary>Stop the jail without removing it (the persistent jail can be re-started later).</summary>
    Task StopAsync(string containerId, CancellationToken ct = default);

    /// <summary>Remove the jail entirely.</summary>
    Task RemoveAsync(string containerId, CancellationToken ct = default);
}
