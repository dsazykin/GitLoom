using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Agents.Sandbox;

/// <summary>Resource ceilings for one agent container (P2-07 §3.1).</summary>
public sealed record SandboxLimits(long MemoryBytes, long Pids)
{
    /// <summary>A conservative default: 2 GiB RAM, 512 pids.</summary>
    public static SandboxLimits Default { get; } = new(2L * 1024 * 1024 * 1024, 512);
}

/// <summary>
/// The two secret tmpfs files mounted at <c>/run/secrets</c> (P2-07 §3.2 + G2 control 1). Both are
/// mode <c>0400</c>; crucially the agent credential file is owned by the <b>agent uid</b> while the
/// OOB session key <c>K</c> is owned by a <b>dedicated supervisor uid ≠ the agent uid</b> — so the
/// prompt-injected agent cannot read <c>K</c> from the file (the memory path is closed by the
/// seccomp denylist + no <c>CAP_SYS_PTRACE</c>). Contents are written after start via an stdin exec,
/// never through <c>Env</c>/argv/persistent disk.
/// </summary>
public sealed record CredTmpfsSpec(
    string CredentialPath,
    string OobKeyPath,
    int Mode,
    int AgentUid,
    int SupervisorUid)
{
    /// <summary>The conventional per-agent credential file (P2-01 injector content).</summary>
    public const string DefaultCredentialPath = "/run/secrets/agent.env";

    /// <summary>The OOB session-HMAC-key file, owned by the supervisor uid.</summary>
    public const string DefaultOobKeyPath = "/run/secrets/oob.key";

    /// <summary>Secret files are read-only to their owner and no one else (G-13).</summary>
    public const int SecretMode = 0b100_000_000; // 0400 octal

    /// <summary>
    /// Builds the spec from the two distinct uids, enforcing G2 control 1 (supervisor uid ≠ agent
    /// uid) at construction — a shared uid would let the agent read <c>K</c> from its own file.
    /// </summary>
    public static CredTmpfsSpec Create(int agentUid, int supervisorUid)
    {
        if (agentUid == supervisorUid)
            throw new SandboxSpecException(
                $"G2 control 1: the OOB key custody uid ({supervisorUid}) must differ from the agent-CLI uid ({agentUid}); a shared uid lets the agent read K.");
        if (agentUid <= 0 || supervisorUid <= 0)
            throw new SandboxSpecException("Both the agent uid and the supervisor uid must be non-root, positive uids.");

        return new CredTmpfsSpec(DefaultCredentialPath, DefaultOobKeyPath, SecretMode, agentUid, supervisorUid);
    }
}

/// <summary>The complete input to <see cref="ContainerSpecBuilder"/> (P2-07 §3.1).</summary>
/// <param name="AdaptersRootPath">The VM-side dynamically-installed-CLI root
/// (<see cref="Adapters.AdapterPaths.VmRoot"/>), bind-mounted READ-ONLY at
/// <see cref="Adapters.AdapterPaths.SandboxMount"/>. Null/empty when no CLIs are installed — the
/// jail simply carries no adapters mount.</param>
public sealed record ContainerSpecRequest(
    string RepoHash,
    string AgentId,
    string WorktreePath,
    string ImageRef,
    SandboxLimits Limits,
    string NetworkName,
    CredTmpfsSpec Credentials,
    string ProxyUrl,
    string UsernsMode = "",
    string? AdaptersRootPath = null);

/// <summary>
/// The pure, unit-testable heart of P2-07: turns an agent request into a hardened Docker
/// <see cref="CreateContainerParameters"/>. It performs <b>no</b> I/O and holds no Docker client;
/// the engine passes the result to <c>CreateContainerAsync</c>.
///
/// <para>Every hardening control is set <b>and re-asserted</b> here (G-11/G-15 + the G2 per-container
/// quartet): a Windows/UNC mount source, a missing seccomp denylist, a present <c>CAP_SYS_PTRACE</c>,
/// or a secret in the environment is a <see cref="SandboxSpecException"/> at construction — the
/// container is never created. <c>kernel.yama.ptrace_scope</c> (G2 control 2) is deliberately
/// <b>not</b> set here: it is a non-namespaced VM-wide sysctl provisioned by the P2-05 bootstrapper
/// (<see cref="GitLoom.Core.Agents.Bootstrap.FirstBootStep"/>).</para>
/// </summary>
public static class ContainerSpecBuilder
{
    /// <summary>Capabilities dropped-then-re-added: a minimal set for dev tooling that never
    /// includes <c>SYS_PTRACE</c> (G2 control 4). We drop <c>ALL</c> and add only these back.</summary>
    private static readonly string[] MinimalCaps =
    {
        "CHOWN", "DAC_OVERRIDE", "FOWNER", "FSETID", "SETGID", "SETUID", "KILL",
    };

    // Windows/WSL mount sources that MUST NEVER be bind-mounted into an agent (G-11).
    private static readonly Regex WslDrvfsMount = new(@"^/mnt/[a-z]/", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsDrive = new(@"^[A-Za-z]:[\\/]", RegexOptions.Compiled);

    /// <summary>The container mount point of the agent worktree.</summary>
    public const string WorkspaceTarget = "/workspace";

    /// <summary>
    /// The mount list: the ext4 worktree, plus the read-only adapters root when one is supplied.
    /// The adapters mount source is an ext4 VM path and goes through the same G-11 rejection.
    /// </summary>
    private static List<Mount> BuildMounts(ContainerSpecRequest request)
    {
        var mounts = new List<Mount>
        {
            new() { Type = "bind", Source = request.WorktreePath, Target = WorkspaceTarget, ReadOnly = false },
        };

        if (!string.IsNullOrEmpty(request.AdaptersRootPath))
        {
            RejectNonExt4Source(request.AdaptersRootPath);
            mounts.Add(new Mount
            {
                Type = "bind",
                Source = request.AdaptersRootPath,
                Target = Adapters.AdapterPaths.SandboxMount,
                // READ-ONLY: agents run the shared CLIs but can never modify what other agents execute.
                ReadOnly = true,
            });
        }

        return mounts;
    }

    /// <summary>Builds the hardened create request; throws typed on any invariant violation.</summary>
    public static CreateContainerParameters Build(ContainerSpecRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // G-11 enforced at CONSTRUCTION: the ONLY mount source is an ext4 worktree path. A drvfs
        // (/mnt/c/...), UNC (\\wsl.localhost\...), or drive-letter (C:\...) source is rejected here
        // so the container is never created.
        RejectNonExt4Source(request.WorktreePath);

        // G2 control 1 is enforced inside CredTmpfsSpec.Create; assert again defensively in case a
        // spec was constructed directly.
        if (request.Credentials.AgentUid == request.Credentials.SupervisorUid)
            throw new SandboxSpecException("G2 control 1: supervisor uid must differ from the agent uid.");

        var env = BuildProxyEnv(request.ProxyUrl);

        var hostConfig = new HostConfig
        {
            // G-15: no privilege escalation, plus the default-deny G2 seccomp profile. NEVER seccomp=unconfined.
            SecurityOpt = new List<string> { "no-new-privileges", SeccompProfile.SecurityOptValue },

            // G2 control 4: drop ALL capabilities and add back a minimal set with no SYS_PTRACE.
            CapDrop = new List<string> { "ALL" },
            CapAdd = MinimalCaps.ToList(),

            // userns remap per the daemon config (empty string = daemon default remap).
            UsernsMode = request.UsernsMode,

            Memory = request.Limits.MemoryBytes,
            PidsLimit = request.Limits.Pids,

            // Read-only rootfs; writable surfaces are tmpfs only.
            ReadonlyRootfs = true,

            // The ext4 worktree at /workspace, plus (when the VM has dynamically installed agent CLIs)
            // the shared adapters root mounted READ-ONLY. The read-only adapters mount is what makes
            // CLI installs DYNAMIC: a CLI installed after provisioning reaches every new sandbox with
            // no image rebuild, while the agent can never tamper with the shared binaries.
            Mounts = BuildMounts(request),

            // Writable scratch + the secrets tmpfs (contents written post-start, never here).
            Tmpfs = new Dictionary<string, string>
            {
                ["/dev/shm"] = "",
                ["/tmp"] = "size=256m,mode=1777",
                // uid/gid MUST name the agent: a tmpfs without them is created root-owned, and mode
                // 0700 then locks the agent out of its OWN $HOME — every agent CLI that writes state
                // under ~/.local or ~/.config (verified: opencode) dies with EACCES on first run.
                // (Same class as the /run/secrets 0711 note below; unhit until a CLI actually ran.)
                ["/home/agent"] = $"size=256m,mode=0700,uid={request.Credentials.AgentUid},gid={request.Credentials.AgentUid}",
                // 0711 (traverse-only, not listable): each uid can reach the secret file it owns —
                // the agent MUST be able to read its own 0400 agent.env — while the per-file 0400
                // ownership still denies the agent uid the supervisor-owned oob.key (G2 control 1).
                // A 0700-root dir would lock the agent out of its own credentials entirely.
                ["/run/secrets"] = "size=1m,mode=0711",
            },

            NetworkMode = request.NetworkName,

            // Belt-and-braces: never privileged (rejection trigger if ever flipped).
            Privileged = false,
        };

        var create = new CreateContainerParameters
        {
            Name = ContainerName(request.RepoHash, request.AgentId),
            Hostname = "agent",
            Image = request.ImageRef,
            User = request.Credentials.AgentUid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WorkingDir = WorkspaceTarget,
            Env = env,
            Labels = new Dictionary<string, string>
            {
                ["gitloom.repo"] = request.RepoHash,
                ["gitloom.agent"] = request.AgentId,
                ["gitloom.role"] = "agent",
            },
            HostConfig = hostConfig,
        };

        // Re-assert the G2 per-container controls on the finished request. Dropping any is a typed
        // builder error, not a warning (rejection trigger: shipping fewer than all four G2 controls).
        AssertG2Controls(create, request.Credentials);
        AssertNoSecretsInEnv(create);

        return create;
    }

    /// <summary>The stable per-repo/per-agent container name (drives the persistent-jail lookup).</summary>
    public static string ContainerName(string repoHash, string agentId)
    {
        var shortHash = repoHash.Length > 12 ? repoHash[..12] : repoHash;
        var safeAgent = new string(agentId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        return $"gitloom-{shortHash}-{safeAgent}";
    }

    private static List<string> BuildProxyEnv(string proxyUrl)
    {
        // Only proxy routing — NEVER a secret (G-13). Both upper- and lower-case forms so every
        // toolchain honours the proxy; NO_PROXY carries loopback + the internal git proxy host.
        return new List<string>
        {
            $"HTTP_PROXY={proxyUrl}",
            $"HTTPS_PROXY={proxyUrl}",
            $"http_proxy={proxyUrl}",
            $"https_proxy={proxyUrl}",
            "NO_PROXY=localhost,127.0.0.1,::1,git.gitloom.internal",
            "no_proxy=localhost,127.0.0.1,::1,git.gitloom.internal",
        };
    }

    private static void RejectNonExt4Source(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new SandboxSpecException("The worktree mount source is empty; an ext4 worktree path is required (G-11).");

        // Any backslash means a Windows/UNC path — an ext4 path never contains one.
        if (source.Contains('\\') || source.StartsWith("//", StringComparison.Ordinal))
            throw new SandboxSpecException($"Refusing UNC/Windows mount source '{source}': only ext4 worktree paths may be mounted (G-11).");

        if (WslDrvfsMount.IsMatch(source))
            throw new SandboxSpecException($"Refusing drvfs mount source '{source}': /mnt/<drive> is a Windows filesystem (G-11).");

        if (WindowsDrive.IsMatch(source))
            throw new SandboxSpecException($"Refusing Windows drive mount source '{source}' (G-11).");
    }

    private static void AssertG2Controls(CreateContainerParameters create, CredTmpfsSpec creds)
    {
        var securityOpt = create.HostConfig.SecurityOpt ?? new List<string>();

        // Control 3: the default-deny seccomp profile is present and NOT unconfined.
        var seccomp = securityOpt.FirstOrDefault(o => o.StartsWith("seccomp=", StringComparison.Ordinal));
        if (seccomp is null)
            throw new SandboxSpecException("G2 control 3: the seccomp denylist is missing from SecurityOpt.");
        if (seccomp.Contains("unconfined", StringComparison.OrdinalIgnoreCase))
            throw new SandboxSpecException("G2 control 3: seccomp=unconfined is forbidden.");
        foreach (var syscall in SeccompProfile.DeniedSyscalls)
            if (!seccomp.Contains(syscall, StringComparison.Ordinal))
                throw new SandboxSpecException($"G2 control 3: the seccomp profile does not deny '{syscall}'.");

        if (!securityOpt.Contains("no-new-privileges"))
            throw new SandboxSpecException("G-15: no-new-privileges is missing from SecurityOpt.");

        // Control 4: no CAP_SYS_PTRACE in the effective set (= what CapAdd restores after dropping ALL).
        var capAdd = create.HostConfig.CapAdd ?? new List<string>();
        if (capAdd.Any(c => c.Contains("SYS_PTRACE", StringComparison.OrdinalIgnoreCase)))
            throw new SandboxSpecException("G2 control 4: CAP_SYS_PTRACE must not be in the agent capability set.");
        var capDrop = create.HostConfig.CapDrop ?? new List<string>();
        if (!capDrop.Any(c => string.Equals(c, "ALL", StringComparison.OrdinalIgnoreCase)))
            throw new SandboxSpecException("G2 control 4: capabilities must be dropped (CapDrop ALL) before any are added back.");

        // Control 1: the supervisor-uid ownership of the K/credential tmpfs is expressed in the spec.
        if (creds.SupervisorUid == creds.AgentUid)
            throw new SandboxSpecException("G2 control 1: supervisor uid must differ from the agent uid.");
        if (creds.Mode != CredTmpfsSpec.SecretMode)
            throw new SandboxSpecException("G2 control 1: the secret tmpfs files must be mode 0400.");

        // Control 2 (ptrace_scope) is VM-wide (P2-05); it MUST NOT appear on the create request.
        AssertNoPtraceScopeSysctl(create);
    }

    private static void AssertNoPtraceScopeSysctl(CreateContainerParameters create)
    {
        // Defensive: Docker.DotNet's HostConfig.Sysctls would carry a per-container sysctl. We never
        // set kernel.yama.ptrace_scope (it is non-namespaced — P2-05's VM-boot job). If a future edit
        // adds it here, fail loudly.
        var sysctls = create.HostConfig.Sysctls;
        if (sysctls is not null && sysctls.Keys.Any(k => k.Contains("ptrace_scope", StringComparison.OrdinalIgnoreCase)))
            throw new SandboxSpecException("kernel.yama.ptrace_scope is VM-wide (P2-05); it must not be set on the container create request.");
    }

    private static void AssertNoSecretsInEnv(CreateContainerParameters create)
    {
        // G-13: the environment carries proxy routing ONLY. Any KEY/TOKEN/SECRET/PASSWORD-shaped var
        // is a leak — the credential path is the 0400 tmpfs, never Env.
        foreach (var entry in create.Env ?? new List<string>())
        {
            var name = entry.Split('=', 2)[0];
            var upper = name.ToUpperInvariant();
            var isProxy = upper is "HTTP_PROXY" or "HTTPS_PROXY" or "NO_PROXY";
            if (isProxy) continue;
            if (upper.Contains("KEY") || upper.Contains("TOKEN") || upper.Contains("SECRET")
                || upper.Contains("PASSWORD") || upper.Contains("CREDENTIAL"))
                throw new SandboxSpecException($"G-13: environment variable '{name}' looks like a secret; secrets go on the 0400 tmpfs, never Env.");
        }
    }
}
