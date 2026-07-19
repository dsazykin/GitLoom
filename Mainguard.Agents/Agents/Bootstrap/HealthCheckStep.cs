using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Step 6: health-check the daemon via <see cref="IDaemonHealthProbe"/> with bounded retries. The
/// bootstrap is complete once the daemon answers healthy — <b>stably</b>: two consecutive healthy
/// probes are required, because a crash-looping daemon (systemd restarts it every few seconds, so a
/// process-existence probe flaps true/false) used to slip one lucky "healthy" past this step and then
/// fail the bootstrapper's post-run re-check with the opaque "state check still failed". On failure
/// the step names the daemon's actual state (unit state + recent journal lines) through the optional
/// <see cref="IDaemonHealthDiagnostics"/> seam, so the error card is actionable.
/// </summary>
public sealed class HealthCheckStep : IBootstrapStep, IBootstrapStepDiagnostics
{
    /// <summary>Healthy answers required in a row before the daemon counts as up — filters the
    /// crash-loop flap where the process momentarily exists between abort-and-restart cycles.</summary>
    public const int RequiredConsecutiveHealthy = 2;

    private readonly IDaemonHealthProbe _probe;
    private readonly IDaemonHealthDiagnostics? _diagnostics;
    private readonly int _attempts;
    private readonly TimeSpan _delay;

    public HealthCheckStep(
        IDaemonHealthProbe probe,
        IDaemonHealthDiagnostics? diagnostics = null,
        int attempts = 30,
        TimeSpan? delay = null)
    {
        _probe = probe;
        _diagnostics = diagnostics;
        _attempts = attempts;
        _delay = delay ?? TimeSpan.FromSeconds(1);
    }

    public string Name => "Health-check daemon";

    public Task<bool> IsSatisfiedAsync(CancellationToken ct) => _probe.IsHealthyAsync(ct);

    /// <summary>Feeds the bootstrapper's post-run re-check failure with the daemon's real state, so
    /// even a flap that slips past the stability window still produces a named reason.</summary>
    public Task<string?> DescribeUnsatisfiedAsync(CancellationToken ct) =>
        _diagnostics?.DescribeUnhealthyAsync(ct) ?? Task.FromResult<string?>(null);

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Waiting for gitloomd to report healthy…");

        // Probe-native wait when available (WslDaemonHealthProbe): the whole consecutive-healthy loop
        // in ONE wsl.exe spawn instead of one per second — per-iteration spawn bursts against a
        // freshly booted GitLoomEnv are what tipped the WSL service into E_UNEXPECTED.
        if (_probe is IDaemonStableHealthWaiter waiter)
        {
            if (await waiter.WaitForStableHealthyAsync(_attempts, RequiredConsecutiveHealthy, ct).ConfigureAwait(false))
            {
                log.Report("Daemon is healthy.");
                return;
            }

            await ThrowUnhealthyAsync(ct).ConfigureAwait(false);
        }

        // Fallback: host-side polling for probes without a native wait (gRPC-backed, test fakes).
        var consecutive = 0;
        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.IsHealthyAsync(ct).ConfigureAwait(false))
            {
                consecutive++;
                if (consecutive >= RequiredConsecutiveHealthy)
                {
                    log.Report("Daemon is healthy.");
                    return;
                }
                log.Report("Daemon answered — confirming it stays up…");
            }
            else
            {
                consecutive = 0;
            }

            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, ct).ConfigureAwait(false);
        }

        await ThrowUnhealthyAsync(ct).ConfigureAwait(false);
    }

    private async Task ThrowUnhealthyAsync(CancellationToken ct)
    {
        var reason = await DescribeUnsatisfiedAsync(ct).ConfigureAwait(false);
        throw new BootstrapException(Name, reason is null
            ? "The Mainguard daemon did not report healthy in time."
            : $"The Mainguard daemon did not report healthy in time. {reason}");
    }
}
