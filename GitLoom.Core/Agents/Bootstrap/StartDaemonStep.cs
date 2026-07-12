using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Step 5: launch <c>gitloomd</c> inside <c>GitLoomEnv</c>. The check phase asks whether the daemon
/// process is already running (<c>pgrep gitloomd</c>) so a rerun on a live VM does not double-start
/// it; the act phase starts the systemd unit the tarball ships.
/// </summary>
public sealed class StartDaemonStep : IBootstrapStep
{
    private readonly IWslRunner _wsl;

    public StartDaemonStep(IWslRunner wsl) => _wsl = wsl;

    public string Name => "Start gitloomd";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.InDistro("pgrep", "-x", "gitloomd"), stdin: null, ct).ConfigureAwait(false);
        return result.Succeeded; // pgrep exits 0 when a match is found
    }

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Starting the gitloomd service…");
        var result = await _wsl.RunAsync(
            WslCommands.InDistroAsRoot("systemctl", "start", "gitloomd"), stdin: null, ct).ConfigureAwait(false);

        if (!result.Succeeded)
            throw new BootstrapException(Name, $"Failed to start gitloomd (exit {result.ExitCode}). {result.StdErr}".Trim());
    }
}
