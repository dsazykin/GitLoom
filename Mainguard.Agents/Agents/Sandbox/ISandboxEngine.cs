using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Sandbox;

/// <summary>The two secrets delivered to a sandbox on spawn — never through <c>Env</c>/argv/disk.</summary>
/// <param name="AgentEnv">The P2-01 credential env-file entries, written to the agent-owned 0400 tmpfs.</param>
/// <param name="OobKey">The OOB session HMAC key <c>K</c>, written to the supervisor-owned 0400 tmpfs.</param>
public sealed record SandboxSecrets(IReadOnlyDictionary<string, string> AgentEnv, byte[] OobKey);

/// <summary>The request to spawn (or re-start) one agent's hardened jail.</summary>
/// <param name="AdaptersRootPath">The VM-side dynamically-installed agent-CLI root, bind-mounted
/// READ-ONLY into the jail so CLIs installed after provisioning reach agents with no image rebuild.
/// Null when no CLIs are installed.</param>
/// <param name="IpcDirPath">The VM-side per-agent IPC dir (daemon Unix socket + the
/// <c>mainguard-agent</c> spawn shim), bind-mounted READ-ONLY at
/// <see cref="Ipc.AgentIpcPaths.SandboxMount"/>. Coordinator-role jails only; null for workers —
/// they get no spawn channel (least privilege).</param>
/// <param name="BareRepoPath">The VM-side bare mirror backing the worktree, bind-mounted at its
/// identical VM path so the linked worktree's <c>gitdir:</c> pointer resolves in-jail (see
/// <see cref="ContainerSpecRequest"/>). Null = no mirror mount.</param>
public sealed record SandboxSpawnRequest(
    string RepoHash,
    string AgentId,
    string WorktreePath,
    string ImageRef,
    SandboxLimits Limits,
    SandboxSecrets Secrets,
    int AgentUid,
    int SupervisorUid,
    string? AdaptersRootPath = null,
    string? IpcDirPath = null,
    string? BareRepoPath = null);

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

    /// <summary>
    /// True when <paramref name="imageRef"/> is present in the engine's image store — the spawn
    /// preflight's probe (field failure 2026-07-17: a fresh/upgraded VM has an empty docker store,
    /// so both jail images are absent and the spawn fails opaquely). The default answers true — an
    /// engine (or test fake) with no separate image store has nothing to preflight;
    /// <see cref="DockerSandboxEngine"/> overrides with a real image inspect.
    /// </summary>
    Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct = default) => Task.FromResult(true);

    /// <summary>
    /// The installed <see cref="SandboxImageVersions.LabelKey"/> label of <paramref name="imageRef"/>.
    /// The spawn preflight compares it to the expected <see cref="SandboxImageVersions"/> constant to
    /// catch a STALE image (right name, old bytes) — the skew class a presence check alone cannot see;
    /// a real <see cref="DockerSandboxEngine"/> answers the actual Docker label, or <c>null</c> when the
    /// image is absent OR carries no such label (an old, pre-versioning image ⇒ stale).
    /// <para>The DEFAULT answers the EXPECTED version (<see cref="SandboxImageVersions.For"/>), so a
    /// storeless engine / test fake — which already reports every image present via the
    /// <see cref="ImageExistsAsync"/> default — passes the version check too (it has no real label
    /// store to be stale against); a fake that wants to exercise the stale path overrides this.</para>
    /// </summary>
    Task<string?> ImageVersionAsync(string imageRef, CancellationToken ct = default) =>
        Task.FromResult(SandboxImageVersions.For(imageRef));

    /// <summary>Run a command inside a live sandbox (e.g. <c>devbox add jq</c>) and return its exit + output.</summary>
    Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default);

    /// <summary>
    /// Freeze every process in the jail (<c>docker pause</c> — SIGSTOP via the freezer cgroup) so the
    /// daemon may safely touch the worktree. This is the P2-09 cooperative-yield <b>timeout</b> path: a
    /// silent agent that never answers <c>[IPC_UPDATE_READY]</c> is paused before any Git mutation.
    /// </summary>
    Task PauseAsync(string containerId, CancellationToken ct = default);

    /// <summary>Resume a paused jail (<c>docker unpause</c>). Called through the yield token on resume.</summary>
    Task UnpauseAsync(string containerId, CancellationToken ct = default);

    /// <summary>Stop the jail without removing it (the persistent jail can be re-started later).</summary>
    Task StopAsync(string containerId, CancellationToken ct = default);

    /// <summary>Remove the jail entirely.</summary>
    Task RemoveAsync(string containerId, CancellationToken ct = default);
}
