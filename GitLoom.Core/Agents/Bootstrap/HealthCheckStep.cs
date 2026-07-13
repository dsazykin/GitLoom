using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Step 6: health-check the daemon's gRPC surface via <see cref="IDaemonHealthProbe"/> (backed by
/// <c>DaemonClient</c> in the App) with bounded retries. The bootstrap is complete once the daemon
/// answers healthy.
/// </summary>
public sealed class HealthCheckStep : IBootstrapStep
{
    private readonly IDaemonHealthProbe _probe;
    private readonly int _attempts;
    private readonly TimeSpan _delay;

    public HealthCheckStep(IDaemonHealthProbe probe, int attempts = 30, TimeSpan? delay = null)
    {
        _probe = probe;
        _attempts = attempts;
        _delay = delay ?? TimeSpan.FromSeconds(1);
    }

    public string Name => "Health-check daemon";

    public Task<bool> IsSatisfiedAsync(CancellationToken ct) => _probe.IsHealthyAsync(ct);

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        log.Report("Waiting for gitloomd to report healthy over gRPC…");
        for (var attempt = 0; attempt < _attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await _probe.IsHealthyAsync(ct).ConfigureAwait(false))
            {
                log.Report("Daemon is healthy.");
                return;
            }
            if (_delay > TimeSpan.Zero)
                await Task.Delay(_delay, ct).ConfigureAwait(false);
        }

        throw new BootstrapException(Name, "The GitLoom daemon did not report healthy in time.");
    }
}
