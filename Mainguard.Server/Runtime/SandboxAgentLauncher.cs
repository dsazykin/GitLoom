using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Git.Exceptions;
using Mainguard.Server.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mainguard.Server.Runtime;

/// <summary>The real, jailed result of a spawn: the container id + whether a stopped jail was reused, plus the ext4 worktree.</summary>
/// <param name="LaunchCommand">The argv that starts the requested agent CLI inside the jail (from the
/// installed adapter's marker), or null when the kind maps to no installed CLI.</param>
public sealed record SandboxLaunchResult(
    string ContainerId, bool Reused, string WorktreePath, IReadOnlyList<string>? LaunchCommand = null);

/// <summary>
/// The daemon-side spawn chain (P2-06 → P2-07) behind <see cref="Services.AgentGrpcService.SpawnAgent"/>,
/// kept out of the gRPC class so the transport layer stays validation+dispatch only. It provisions the
/// per-agent worktree off the repo's bare mirror (<see cref="IAgentEnvironment.Worktrees"/>), ensures the
/// default-deny egress network + proxy exist (<see cref="IAgentEnvironment.Egress"/>), and starts the
/// hardened container (<see cref="IAgentEnvironment.Sandboxes"/>), returning the real container id.
///
/// <para><b>Graceful degradation (why no throwing stub):</b> when the repo is <i>not</i> provisioned there
/// is no bare mirror to branch a worktree from and nothing to jail, so <see cref="TryLaunchAsync"/> returns
/// <c>null</c> and the caller keeps a session-only record. This is the headless path the in-proc Alpha loop
/// smoke rides (no Docker), while a provisioned repo on a Docker host takes the real jail path — the leg the
/// <c>SandboxSpawnDockerTests</c> RequiresDocker test verifies in CI.</para>
/// </summary>
public sealed class SandboxAgentLauncher
{
    private const int AgentUid = 1000;
    private const int SupervisorUid = 1001;

    private readonly IAgentEnvironment _environment;
    private readonly string _imageRef;
    private readonly InstalledAdapterCatalog _adapters;
    private readonly ILogger _log;

    public SandboxAgentLauncher(
        IAgentEnvironment environment, InstalledAdapterCatalog? adapters = null, ILoggerFactory? loggerFactory = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        // Single source with the provisioner (SandboxImages.AgentBase) so the daemon preflights/spawns
        // exactly the tag the app builds/labels — including a MAINGUARD_AGENT_IMAGE override.
        _imageRef = SandboxImageVersions.AgentBaseRef();
        // The dynamically installed CLIs (the user's OOBE/settings choices), read fresh per spawn so a
        // CLI installed while the daemon runs is immediately launchable.
        _adapters = adapters ?? new InstalledAdapterCatalog();
        // Optional so the RequiresDocker direct-construction tests keep working; DI supplies the real one.
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger(DaemonLogCategories.Spawn);
    }

    /// <summary>
    /// Provisions the worktree and starts the hardened jail for <paramref name="agentId"/> against the
    /// repo identified by <paramref name="repoHandle"/>. Returns the real container handle, or <c>null</c>
    /// when the repo is not provisioned (session-only path). On a failure <i>after</i> the worktree exists,
    /// the half-made worktree is cleaned up so no residue survives, then the failure propagates.
    /// </summary>
    public async Task<SandboxLaunchResult?> TryLaunchAsync(
        string repoHandle, string agentId, string agentKind, string? modelApiKey,
        string? ipcDirPath = null, CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        _log.LogInformation("launch begin: repo={Repo} kind={Kind}", repoHandle, agentKind);

        var barePath = _environment.Repos.BareRepoPathFor(repoHandle);
        if (!Directory.Exists(barePath))
        {
            // Repo not provisioned — nothing to branch a worktree from, nothing to jail. The caller keeps
            // a session-only record (the daemon still tracks/streams/stops it) rather than fabricating a jail.
            _log.LogInformation("repo not provisioned — session-only (no jail): repo={Repo}", repoHandle);
            return null;
        }

        // Spawn preflight (field failure 2026-07-17, twice): a fresh MainguardEnv import AND the
        // tier-2 VM upgrade both leave the docker image store empty (it lives outside /home/mainguard,
        // so the migration correctly skips it). Verify BOTH jail images BEFORE any worktree/jail is
        // made — present AND current (the mainguard.image.version label == the expected constant) — so
        // the failure is one typed, actionable error naming the missing/outdated image instead of a
        // DockerImageNotFoundException at container-create (agent-base), an opaque create failure
        // inside Egress.EnsureReadyAsync (egress-proxy), or a silently-stale image running old bytes.
        var problems = new List<SandboxImagePreflightProblem>();
        foreach (var imageRef in new[] { _imageRef, EgressProxyConfigurator.DefaultImageRef })
        {
            if (!await _environment.Sandboxes.ImageExistsAsync(imageRef, ct).ConfigureAwait(false))
            {
                problems.Add(new SandboxImagePreflightProblem(imageRef, Stale: false));
                continue;
            }

            // An image we don't version (a fully-renamed MAINGUARD_AGENT_IMAGE override) is
            // presence-only — we have no expected hash to compare against.
            var expected = SandboxImageVersions.For(imageRef);
            if (expected is null)
            {
                continue;
            }

            var installed = await _environment.Sandboxes.ImageVersionAsync(imageRef, ct).ConfigureAwait(false);
            if (!string.Equals(installed, expected, StringComparison.Ordinal))
            {
                problems.Add(new SandboxImagePreflightProblem(imageRef, Stale: true));
            }
        }

        if (problems.Count > 0)
        {
            _log.LogError("preflight failed: sandbox image(s) need provisioning: {Images}",
                string.Join(", ", problems.Select(p => $"{p.ImageRef} ({(p.Stale ? "stale" : "missing")})")));
            throw new SandboxImageMissingException(problems);
        }

        _log.LogInformation("preflight ok: jail images present and current");

        // agentKind → the CLI the user dynamically installed. Resolved BEFORE the worktree so an
        // unknown kind costs nothing; the jail still spawns without a launch command (the operator
        // gets a shell in a correct sandbox rather than a failed spawn), and the caller surfaces it.
        var adapter = _adapters.TryGet(agentKind);
        var launchCommand = adapter?.Launch;

        var worktreePath = _environment.Worktrees.CreateAgentWorktree(repoHandle, agentId);
        _log.LogInformation("worktree ready: {Path}", worktreePath);
        try
        {
            // The default-deny network + allowlist proxy must exist before the jail joins the network.
            await _environment.Egress.EnsureReadyAsync(ct).ConfigureAwait(false);
            _log.LogInformation("egress ready (default-deny network + proxy)");

            var secrets = BuildSecrets(modelApiKey, adapter, extraEnv);
            var handle = await _environment.Sandboxes.SpawnAsync(new SandboxSpawnRequest(
                RepoHash: repoHandle,
                AgentId: agentId,
                WorktreePath: worktreePath,
                ImageRef: _imageRef,
                Limits: SandboxLimits.Default,
                Secrets: secrets,
                AgentUid: AgentUid,
                SupervisorUid: SupervisorUid,
                // Mount the shared CLI root read-only ONLY when CLIs are actually installed.
                AdaptersRootPath: _adapters.HasAny() ? AdapterPaths.VmRoot : null,
                // Coordinator-role jails only: the daemon-served spawn-channel dir (read-only mount).
                IpcDirPath: ipcDirPath,
                // The bare mirror at its identical VM path so the worktree's gitdir pointer resolves
                // in-jail (field bug 2026-07-23: every in-jail git command died "not a git repository").
                BareRepoPath: barePath), ct).ConfigureAwait(false);

            _log.LogInformation(
                "jail started: container={Container} reused={Reused} launchCmd={HasLaunch}",
                handle.ContainerId, handle.Reused, launchCommand is { Count: > 0 });
            return new SandboxLaunchResult(handle.ContainerId, handle.Reused, worktreePath, launchCommand);
        }
        catch (Exception ex)
        {
            // Leave no residue: remove the worktree we just created before surfacing the failure.
            _log.LogError(ex, "jail start failed after worktree — cleaning up worktree: repo={Repo}", repoHandle);
            TryRemoveWorktree(repoHandle, agentId);
            throw;
        }
    }

    /// <summary>Best-effort teardown of a launched agent: remove the jail, then its worktree. Never throws.</summary>
    public async Task TeardownAsync(string? repoHash, string agentId, string containerId, CancellationToken ct = default)
    {
        try { await _environment.Sandboxes.RemoveAsync(containerId, ct).ConfigureAwait(false); }
        catch { /* never fail a stop from teardown */ }

        if (!string.IsNullOrEmpty(repoHash))
        {
            TryRemoveWorktree(repoHash, agentId);
        }
    }

    private void TryRemoveWorktree(string repoHash, string agentId)
    {
        try { _environment.Worktrees.RemoveAgentWorktree(repoHash, agentId, force: true); }
        catch { /* best effort */ }
    }

    /// <summary>
    /// The credential env-file entries (P2-01), written to the agent-owned 0400 tmpfs — never
    /// Env/argv/disk. The variable NAME comes from the installed adapter's marker (audit fix #13 —
    /// a hardcoded <c>ANTHROPIC_API_KEY</c> meant codex/opencode never saw their keys):
    /// <list type="bullet">
    ///   <item>marker declares <c>apiKeyEnvVar</c> → the key is injected under that name;</item>
    ///   <item>marker declares NONE → the CLI authenticates interactively; no key is injected;</item>
    ///   <item>no marker at all (unknown kind / dev box without a catalog) → the legacy
    ///   <c>ANTHROPIC_API_KEY</c> fallback keeps local-dev flows working.</item>
    /// </list>
    /// The user's custom <paramref name="extraEnv"/> entries (llm_env_* — multi-provider CLIs like
    /// opencode) ride the same env-file; on a name collision the adapter's declared key wins.
    /// </summary>
    internal static SandboxSecrets BuildSecrets(
        string? modelApiKey, InstalledAdapterMarker? adapter,
        IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        var agentEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extraEnv is not null)
        {
            foreach (var (name, value) in extraEnv)
            {
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrEmpty(value))
                {
                    agentEnv[name] = value;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(modelApiKey))
        {
            var envVar = adapter is null ? "ANTHROPIC_API_KEY" : adapter.ApiKeyEnvVar;
            if (envVar is { Length: > 0 })
            {
                agentEnv[envVar] = modelApiKey;
            }
        }

        var oobKey = new byte[32];
        RandomNumberGenerator.Fill(oobKey);
        return new SandboxSecrets(agentEnv, oobKey);
    }
}
