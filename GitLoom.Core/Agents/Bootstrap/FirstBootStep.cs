using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Step 4 (first boot): provision the VM-wide sysctls the sandbox depends on and wait for Docker.
/// <para>
/// <b>G2 control (2) — boot-provisioned here.</b> <c>kernel.yama.ptrace_scope=2</c> is a
/// non-namespaced kernel sysctl: Docker permits only namespaced sysctls, so it CANNOT be set per
/// container from <c>CreateContainerAsync</c>. It must be set VM-wide at first boot (alongside
/// <c>fs.inotify.max_user_watches</c>) — this step both applies it live and persists it to
/// <c>/etc/sysctl.d/</c>, and its <b>check</b> phase asserts the current value is ≥ 2 so a regressed
/// VM re-provisions. P2-07's key-custody guarantee names this check as its dependency.
/// </para>
/// </summary>
public sealed class FirstBootStep : IBootstrapStep
{
    /// <summary>Watches raised so large agent worktrees don't exhaust inotify.</summary>
    public const string InotifyWatches = "fs.inotify.max_user_watches=524288";

    /// <summary>G2 control (2): yama ptrace scope hardened VM-wide.</summary>
    public const string PtraceScope = "kernel.yama.ptrace_scope=2";

    /// <summary>Where both sysctls are persisted so they survive a VM restart.</summary>
    public const string SysctlDropInPath = "/etc/sysctl.d/99-gitloom-sandbox.conf";

    private const int RequiredWatches = 524288;
    private const int RequiredPtraceScope = 2;

    private readonly IWslRunner _wsl;
    private readonly int _dockerPollAttempts;
    private readonly TimeSpan _dockerPollDelay;

    public FirstBootStep(IWslRunner wsl, int dockerPollAttempts = 90, TimeSpan? dockerPollDelay = null)
    {
        _wsl = wsl;
        _dockerPollAttempts = dockerPollAttempts;
        _dockerPollDelay = dockerPollDelay ?? TimeSpan.FromSeconds(1);
    }

    public string Name => "First boot (sysctls + Docker)";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        // G2 control (2): a VM where ptrace_scope regressed below 2 is NOT satisfied — it re-provisions.
        var ptrace = await ReadSysctlIntAsync("kernel.yama.ptrace_scope", ct).ConfigureAwait(false);
        if (ptrace is null || ptrace < RequiredPtraceScope)
            return false;

        var watches = await ReadSysctlIntAsync("fs.inotify.max_user_watches", ct).ConfigureAwait(false);
        if (watches is null || watches < RequiredWatches)
            return false;

        return await DockerIsGreenAsync(ct).ConfigureAwait(false);
    }

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        // Apply live.
        log.Report("Raising fs.inotify.max_user_watches…");
        await RunRootAsync(ct, "sysctl", "-w", InotifyWatches).ConfigureAwait(false);

        log.Report("Hardening kernel.yama.ptrace_scope=2 (G2)…");
        await RunRootAsync(ct, "sysctl", "-w", PtraceScope).ConfigureAwait(false);

        // Persist BOTH to /etc/sysctl.d/ so they survive a VM restart. `tee` reads stdin natively —
        // no shell, no redirect.
        log.Report($"Persisting sysctls to {SysctlDropInPath}…");
        var dropIn = $"{InotifyWatches}\n{PtraceScope}\n";
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", SysctlDropInPath), stdin: dropIn, ct).ConfigureAwait(false);

        // Make sure dockerd is actually up (repairs wsl.conf + clears a stale pidfile, then starts it).
        await EnsureDockerRunningAsync(log, ct).ConfigureAwait(false);

        // Wait for the Docker socket to come up. First boot can race: dockerd's bolt volume-metadata
        // DB open times out under fresh-VM I/O contention, dockerd exits, and systemd's restart loop
        // then backs off after a few rapid failures with "start request repeated too quickly" — after
        // which NOTHING retries it, so a plain wait can sit idle for minutes. So we poll patiently AND
        // periodically clear the failed/locked-out unit and re-start it, which gets dockerd going again
        // as soon as the transient contention clears.
        log.Report("Waiting for Docker to become ready…");
        for (var attempt = 0; attempt < _dockerPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await DockerIsGreenAsync(ct).ConfigureAwait(false))
            {
                log.Report("Docker is ready.");
                return;
            }
            // Every ~10 polls, nudge docker past systemd's rapid-restart lockout — but only when the unit
            // has actually died/locked out (failed/inactive), never while it is slowly "activating", so a
            // slow-but-healthy start is not interrupted.
            if (attempt > 0 && attempt % 10 == 0)
            {
                var active = await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "is-active", "docker"), stdin: null, ct).ConfigureAwait(false);
                var s = active.StdOut.Trim();
                if (s is "failed" or "inactive")
                {
                    await RunRootAsync(ct, "rm", "-f", "/var/run/docker.pid").ConfigureAwait(false);
                    await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "reset-failed", "docker"), stdin: null, ct).ConfigureAwait(false);
                    await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "start", "docker"), stdin: null, ct).ConfigureAwait(false);
                }
            }
            if (_dockerPollDelay > TimeSpan.Zero)
                await Task.Delay(_dockerPollDelay, ct).ConfigureAwait(false);
        }

        throw new BootstrapException(Name,
            $"Docker did not become ready inside {WslCommands.DistroName}. {await DescribeDockerFailureAsync(ct).ConfigureAwait(false)}".Trim());
    }

    /// <summary>Gathers the real reason dockerd didn't come up — its own journal failure line(s) plus
    /// `docker info`'s error — so the OOBE error card shows something actionable, not just a bare
    /// "did not become ready".</summary>
    private async Task<string> DescribeDockerFailureAsync(CancellationToken ct)
    {
        var journal = await _wsl.RunAsync(
            WslCommands.InDistroAsRoot("journalctl", "-u", "docker", "--no-pager", "-n", "25"),
            stdin: null, ct).ConfigureAwait(false);

        // Prefer dockerd's explicit failure line (e.g. the bolt volume-DB timeout); fall back to `docker
        // info`'s stderr, then a trimmed journal tail.
        var failure = journal.StdOut
            .Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Contains("failed to start daemon", StringComparison.OrdinalIgnoreCase)
                             || l.Contains("level=fatal", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(failure))
            return failure;

        var info = await _wsl.RunAsync(WslCommands.InDistro("docker", "info"), stdin: null, ct).ConfigureAwait(false);
        var detail = new[] { info.StdErr, info.StdOut }.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))?.Trim();
        return string.IsNullOrEmpty(detail) ? "Check `journalctl -u docker` inside the VM for details." : detail;
    }

    /// <summary>
    /// Brings dockerd up reliably and idempotently. The tarball enables <c>docker.service</c> under
    /// systemd, so dockerd starts on boot on its own; a leftover <c>[boot] command = service docker
    /// start</c> in <c>/etc/wsl.conf</c> would ALSO start it, double-starting dockerd → a stale
    /// <c>/var/run/docker.pid</c> → <c>"pid file found"</c> and systemd's <c>"start request repeated too
    /// quickly"</c>. So we rewrite wsl.conf deterministically with NO boot command (writing the WHOLE
    /// file is idempotent — the previous logic <b>appended</b> a <c>[boot] command</c> whenever it
    /// didn't spot the literal <c>"dockerd"</c>, which it never did, so every retry duplicated
    /// <c>boot.command</c> until WSL rejected the file), then clear any stale pidfile / failed-unit
    /// lockout and (re)start docker via systemd in the current session. The poll loop that follows is
    /// the source of truth for readiness, so transient start hiccups here are non-fatal.
    /// </summary>
    private async Task EnsureDockerRunningAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Repairing /etc/wsl.conf (systemd, no duplicate boot command)…");
        const string wslConf = "[boot]\nsystemd=true\n\n[user]\ndefault=gitloom\n";
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", "/etc/wsl.conf"), stdin: wslConf, ct).ConfigureAwait(false);

        // Pin dockerd to a dedicated bridge subnet + address pool. All WSL2 distros share ONE network
        // stack, so GitLoomEnv's dockerd defaulting docker0 to 172.17.0.0/16 collides with Docker
        // Desktop's docker0 — which drops the user's Docker Desktop AND wedges this daemon (the loser of
        // the race restart-loops and a lingering instance then holds the volume-DB lock → the boltdb
        // "timeout"). A distinct 10.202/10.203 range never collides with Docker Desktop (172.x) or a
        // typical LAN. Written idempotently; the (re)start below picks it up.
        var alreadyConfigured = await DaemonJsonMatchesAsync(ct).ConfigureAwait(false);
        if (!alreadyConfigured)
        {
            await _wsl.RunAsync(WslCommands.InDistroAsRoot("mkdir", "-p", "/etc/docker"), stdin: null, ct).ConfigureAwait(false);
            await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", "/etc/docker/daemon.json"), stdin: DockerDaemonJson, ct).ConfigureAwait(false);
        }

        // Skip the restart only when the config is already ours AND the daemon is healthy — otherwise a
        // daemon left running on the colliding default subnet would never be moved onto the safe one.
        if (alreadyConfigured && await DockerIsGreenAsync(ct).ConfigureAwait(false))
        {
            log.Report("Docker is already running.");
            return;
        }

        log.Report("Starting Docker…");
        // Stop first so systemd SIGTERMs the whole unit cgroup — this kills any lingering/half-dead
        // dockerd holding the volume-DB lock. Then clear the stale pidfile + rapid-restart lockout and
        // start clean, now with the dedicated-subnet daemon.json in effect.
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "stop", "docker"), stdin: null, ct).ConfigureAwait(false);
        await RunRootAsync(ct, "rm", "-f", "/var/run/docker.pid").ConfigureAwait(false);
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "reset-failed", "docker"), stdin: null, ct).ConfigureAwait(false);
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "start", "docker"), stdin: null, ct).ConfigureAwait(false);
    }

    /// <summary>The dedicated Docker network config baked into GitLoomEnv so its dockerd never collides
    /// with a concurrently-running Docker Desktop in the shared WSL2 network stack.</summary>
    public const string DockerDaemonJson =
        "{\n  \"bip\": \"10.202.0.1/24\",\n  \"default-address-pools\": [ { \"base\": \"10.203.0.0/16\", \"size\": 24 } ]\n}\n";

    private async Task<bool> DaemonJsonMatchesAsync(CancellationToken ct)
    {
        var current = await _wsl.RunAsync(WslCommands.InDistro("cat", "/etc/docker/daemon.json"), stdin: null, ct).ConfigureAwait(false);
        return current.Succeeded && current.StdOut.Contains("10.202.0.1/24", StringComparison.Ordinal);
    }

    private async Task<int?> ReadSysctlIntAsync(string key, CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.InDistro("sysctl", "-n", key), stdin: null, ct).ConfigureAwait(false);
        if (!result.Succeeded)
            return null;
        return int.TryParse(result.StdOut.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private async Task<bool> DockerIsGreenAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.InDistro("docker", "info"), stdin: null, ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    private Task<WslRunResult> RunRootAsync(CancellationToken ct, params string[] command) =>
        _wsl.RunAsync(WslCommands.InDistroAsRoot(command), stdin: null, ct);
}
