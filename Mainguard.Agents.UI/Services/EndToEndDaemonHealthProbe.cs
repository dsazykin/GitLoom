using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Daemon;

namespace Mainguard.Agents.UI.Services;

/// <summary>The transport leg's verdict: reachable-and-authenticated, or a named failure.</summary>
public sealed record DaemonTransportHealth(bool Healthy, string? Failure = null);

/// <summary>
/// The OOBE's daemon health gate, hardened end-to-end (audit fix #9): "healthy" now means the
/// <b>Windows app itself</b> completed an authenticated gRPC call over loopback — not merely that a
/// <c>mainguardd</c> process exists inside the VM. The old process-existence probe passed while the
/// client couldn't read the session token or reach the relayed port, so setup reported Done on a
/// machine where the control center could never talk to the daemon.
///
/// <para>Two legs, both required:</para>
/// <list type="number">
///   <item><b>In-VM</b> (<see cref="WslDaemonHealthProbe"/>): the process is up and stable. Runs
///   first — it boots the VM if needed and its diagnostics name a crash-loop's actual reason.</item>
///   <item><b>Transport</b>: an authenticated <c>ListAgents</c> from this process over
///   <c>127.0.0.1:5250</c> with the <see cref="DaemonTokenLocator"/>-resolved token. Its failure
///   text distinguishes "no token found", "token rejected", and "port unreachable" — each of which
///   previously surfaced as nothing at all.</item>
/// </list>
/// </summary>
public sealed class EndToEndDaemonHealthProbe : IDaemonHealthProbe, IDaemonHealthDiagnostics, IDaemonStableHealthWaiter
{
    private static readonly TimeSpan TransportDeadline = TimeSpan.FromSeconds(5);

    /// <summary>Transport probes to allow after the process leg is stable — the daemon binds its
    /// port right at startup, but a just-started daemon can beat the client by a beat or two.</summary>
    private const int TransportAttempts = 5;

    private readonly WslDaemonHealthProbe _inVm;
    private readonly Func<CancellationToken, Task<DaemonTransportHealth>> _transport;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private string? _lastTransportFailure;

    public EndToEndDaemonHealthProbe(
        IWslRunner wsl,
        Func<CancellationToken, Task<DaemonTransportHealth>>? transport = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _inVm = new WslDaemonHealthProbe(wsl ?? throw new ArgumentNullException(nameof(wsl)));
        _transport = transport ?? ProbeTransportAsync;
        _delay = delay ?? Task.Delay;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        if (!await _inVm.IsHealthyAsync(ct).ConfigureAwait(false))
        {
            return false;
        }

        return await TransportHealthyAsync(attempts: 1, ct).ConfigureAwait(false);
    }

    public async Task<bool> WaitForStableHealthyAsync(int attempts, int requiredConsecutive, CancellationToken ct)
    {
        if (!await _inVm.WaitForStableHealthyAsync(attempts, requiredConsecutive, ct).ConfigureAwait(false))
        {
            return false;
        }

        return await TransportHealthyAsync(TransportAttempts, ct).ConfigureAwait(false);
    }

    public async Task<string?> DescribeUnhealthyAsync(CancellationToken ct)
    {
        // The process leg's diagnosis (unit state + journal tail) wins when the process is down —
        // that is the daemon's ACTUAL failure. When the process is fine, the transport failure is
        // the story (token / port), which the in-VM probe cannot see.
        if (!await _inVm.IsHealthyAsync(ct).ConfigureAwait(false))
        {
            return await _inVm.DescribeUnhealthyAsync(ct).ConfigureAwait(false);
        }

        return _lastTransportFailure is { Length: > 0 } failure
            ? $"mainguardd is running inside {WslCommands.DistroName}, but this app could not complete "
              + $"an authenticated call to it: {failure}"
            : null;
    }

    private async Task<bool> TransportHealthyAsync(int attempts, CancellationToken ct)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var health = await _transport(ct).ConfigureAwait(false);
            if (health.Healthy)
            {
                _lastTransportFailure = null;
                return true;
            }

            _lastTransportFailure = health.Failure;
            if (attempt < attempts - 1)
            {
                await _delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    /// <summary>The real transport leg: token via <see cref="DaemonTokenLocator"/>, one authenticated
    /// <c>ListAgents</c> over loopback. Every failure is mapped to a named, actionable sentence.</summary>
    private static async Task<DaemonTransportHealth> ProbeTransportAsync(CancellationToken ct)
    {
        var token = DaemonTokenLocator.TryReadToken();
        if (token is null)
        {
            return new DaemonTransportHealth(false,
                "no session token was found (probed: "
                + string.Join(", ", DaemonTokenLocator.CandidatePaths())
                + "). The daemon writes it on startup — if it is running, the token file location moved.");
        }

        try
        {
            using var client = DaemonClient.ForLoopback();
            await client.ListAgentsAsync(ct, TransportDeadline).ConfigureAwait(false);
            return new DaemonTransportHealth(true);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
        {
            return new DaemonTransportHealth(false,
                "the daemon rejected the session token (it may have restarted since the token was read; "
                + "retrying usually heals this).");
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            return new DaemonTransportHealth(false,
                $"127.0.0.1:{DaemonPaths.DefaultLoopbackPort} was unreachable ({ex.StatusCode}). "
                + "Check that no other program occupies the port and that WSL localhost forwarding "
                + "is not disabled in .wslconfig.");
        }
        catch (Exception ex)
        {
            return new DaemonTransportHealth(false, ex.Message);
        }
    }
}
