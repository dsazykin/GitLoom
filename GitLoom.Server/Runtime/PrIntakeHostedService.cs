using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Orchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GitLoom.Server.Runtime;

/// <summary>
/// P2-13 carried-in from P2-12 (b): the daemon scheduler slot that drives the external-PR intake
/// poll loop. <see cref="ExternalPrIntake.RunAsync"/> is the engine; nothing called it before. This
/// hosted service starts it as part of the daemon lifecycle and cancels it on stop.
///
/// The intake service is resolved optionally: the poll loop only runs once an
/// <see cref="IExternalPrIntake"/> (with its provider/worktree/fetcher/target-resolver chain and at
/// least one subscription source) is registered. Until that dependency graph + subscription config is
/// wired daemon-side, this idles rather than crashing the host — the scheduler is in place and lights
/// up the moment intake is configured.
/// </summary>
public sealed class PrIntakeHostedService : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _stopped;

    public PrIntakeHostedService(IServiceProvider services) => _services = services;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var intake = _services.GetService<IExternalPrIntake>();
        if (intake is null)
            return Task.CompletedTask; // no intake configured yet — scheduler idles, never crashes.

        _loop = intake.RunAsync(_cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception) { /* a poll-loop fault must not fail daemon shutdown */ }
        }
        _cts.Dispose();
    }
}
