using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using Microsoft.Extensions.Hosting;

namespace GitLoom.Server.Runtime;

/// <summary>
/// Runs the P2-08 daemon boot work: on start it executes the RT-D1 ordered
/// <see cref="DaemonBootSequence"/> (merge-reconcile slot first, then the swarm reconciler) and starts
/// the gateway's token-bucket pump loop; on stop it cancels the pump. Boot reconciliation is
/// best-effort — a failure is swallowed so a Docker hiccup cannot stop the gRPC surface from serving.
/// </summary>
public sealed class GatewayHostedService : IHostedService
{
    private static readonly TimeSpan PumpInterval = TimeSpan.FromMilliseconds(50);

    private readonly DaemonBootSequence _bootSequence;
    private readonly AiGateway _gateway;
    private readonly CancellationTokenSource _cts = new();
    private Task? _pumpLoop;
    private int _stopped;

    public GatewayHostedService(DaemonBootSequence bootSequence, AiGateway gateway)
    {
        _bootSequence = bootSequence;
        _gateway = gateway;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _bootSequence.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort boot reconciliation — never block the daemon from serving on it.
        }

        _pumpLoop = _gateway.RunPumpLoopAsync(PumpInterval, _cts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // StopAsync can be invoked more than once during host teardown (e.g. WebApplicationFactory
        // disposal after the host has already stopped). Guard so we never Cancel/Dispose the
        // CancellationTokenSource twice — the second call would throw ObjectDisposedException.
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _cts.Cancel();
        if (_pumpLoop is not null)
        {
            try
            {
                await _pumpLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        _cts.Dispose();
    }
}
