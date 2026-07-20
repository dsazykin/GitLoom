using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Step 5: launch <c>mainguardd</c> inside <c>MainguardEnv</c>. The check phase asks whether the daemon
/// process is already running (<c>pgrep mainguardd</c>) so a rerun on a live VM does not double-start
/// it; the act phase starts the systemd unit the tarball ships.
/// </summary>
public sealed class StartDaemonStep : IBootstrapStep, IBootstrapStepDiagnostics
{
    private readonly IWslRunner _wsl;
    private readonly IDaemonHealthDiagnostics? _diagnostics;

    public StartDaemonStep(IWslRunner wsl, IDaemonHealthDiagnostics? diagnostics = null)
    {
        _wsl = wsl;
        _diagnostics = diagnostics;
    }

    public string Name => "Start mainguardd";

    /// <summary>When the post-start re-check finds no <c>mainguardd</c> process (e.g. it started and
    /// immediately crash-looped), name the unit state + journal tail instead of the generic
    /// "state check still failed".</summary>
    public Task<string?> DescribeUnsatisfiedAsync(CancellationToken ct) =>
        _diagnostics?.DescribeUnhealthyAsync(ct) ?? Task.FromResult<string?>(null);

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.InDistro("pgrep", "-x", "mainguardd"), stdin: null, ct).ConfigureAwait(false);
        return result.Succeeded; // pgrep exits 0 when a match is found
    }

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Starting the mainguardd service…");
        var result = await _wsl.RunAsync(
            WslCommands.InDistroAsRoot("systemctl", "start", "mainguardd"), stdin: null, ct).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new BootstrapException(Name, $"Failed to start mainguardd (exit {result.ExitCode}). {result.StdErr}".Trim());
    }
}
