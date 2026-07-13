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

    public FirstBootStep(IWslRunner wsl, int dockerPollAttempts = 30, TimeSpan? dockerPollDelay = null)
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

        // Ensure the tarball's /etc/wsl.conf [boot] dockerd command is present; repair if missing.
        await EnsureDockerdBootCommandAsync(log, ct).ConfigureAwait(false);

        // Wait for the Docker socket to come up.
        log.Report("Waiting for Docker to become ready…");
        for (var attempt = 0; attempt < _dockerPollAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await DockerIsGreenAsync(ct).ConfigureAwait(false))
            {
                log.Report("Docker is ready.");
                return;
            }
            if (_dockerPollDelay > TimeSpan.Zero)
                await Task.Delay(_dockerPollDelay, ct).ConfigureAwait(false);
        }

        var info = await _wsl.RunAsync(WslCommands.InDistro("docker", "info"), stdin: null, ct).ConfigureAwait(false);
        throw new BootstrapException(Name, $"Docker did not become ready inside {WslCommands.DistroName}. {info.StdErr}".Trim());
    }

    private async Task EnsureDockerdBootCommandAsync(IProgress<string> log, CancellationToken ct)
    {
        var wslConf = await _wsl.RunAsync(WslCommands.InDistro("cat", "/etc/wsl.conf"), stdin: null, ct).ConfigureAwait(false);
        if (wslConf.Succeeded && wslConf.StdOut.Contains("dockerd", StringComparison.Ordinal))
            return; // tarball already ships it

        log.Report("Repairing /etc/wsl.conf [boot] dockerd command…");
        var bootBlock = "[boot]\ncommand = service docker start\n";
        await _wsl.RunAsync(WslCommands.InDistroAsRoot("tee", "-a", "/etc/wsl.conf"), stdin: bootBlock, ct).ConfigureAwait(false);
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
