using System;
using System.Globalization;
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
            // Every ~10 polls, nudge docker past systemd's rapid-restart lockout.
            if (attempt > 0 && attempt % 10 == 0)
            {
                await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "reset-failed", "docker"), stdin: null, ct).ConfigureAwait(false);
                await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "start", "docker"), stdin: null, ct).ConfigureAwait(false);
            }
            if (_dockerPollDelay > TimeSpan.Zero)
                await Task.Delay(_dockerPollDelay, ct).ConfigureAwait(false);
        }

        var info = await _wsl.RunAsync(WslCommands.InDistro("docker", "info"), stdin: null, ct).ConfigureAwait(false);
        throw new BootstrapException(Name, $"Docker did not become ready inside {WslCommands.DistroName}. {info.StdErr}".Trim());
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

        log.Report("Starting Docker…");
        // A stale pidfile from a prior double-start makes dockerd refuse to boot; remove it first.
        await RunRootAsync(ct, "rm", "-f", "/var/run/docker.pid").ConfigureAwait(false);
        // reset-failed clears the "start request repeated too quickly" lockout, then start via systemd.
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "reset-failed", "docker"), stdin: null, ct).ConfigureAwait(false);
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("systemctl", "start", "docker"), stdin: null, ct).ConfigureAwait(false);
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
