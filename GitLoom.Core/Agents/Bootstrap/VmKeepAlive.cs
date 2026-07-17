using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Holds the <c>GitLoomEnv</c> distro awake for this object's lifetime. WSL2 idle-terminates a
/// distro shortly (~15–60 s) after its last <c>wsl.exe</c> client exits — an established gRPC
/// connection through the localhost relay does <b>not</b> count as a client — which cleanly
/// <c>systemctl stop</c>s <c>gitloomd</c> out from under the app between RPCs (and once killed it
/// mid-migration, orphaning the EF migration lock: see <c>DatabaseBootstrapTests</c>). Waking the
/// VM is not enough; something must <b>keep</b> it awake, and the only reliable holder is a live
/// <c>wsl.exe</c> session.
///
/// <para>This runs <c>wsl.exe -d GitLoomEnv --exec sleep infinity</c> (hidden, no window) and
/// restarts it with capped backoff when it exits — the distro may not be imported yet (pre-OOBE),
/// or may be unregistered/re-imported mid-run; the holder simply reacquires when it can. A holder
/// that stayed up past <see cref="_healthyUptime"/> resets the backoff. Dispose kills the holder
/// process; the distro then idles out on WSL's own policy. Scoped to <c>GitLoomEnv</c> only —
/// never a VM-wide lifecycle verb (G-12).</para>
/// </summary>
public sealed class VmKeepAlive : IDisposable
{
    private static readonly TimeSpan DefaultBackoffBase = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DefaultBackoffCap = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultHealthyUptime = TimeSpan.FromSeconds(60);

    private readonly Func<CancellationToken, Task> _runHolderOnce;
    private readonly TimeSpan _backoffBase;
    private readonly TimeSpan _backoffCap;
    private readonly TimeSpan _healthyUptime;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>The shipped holder: one hidden <c>wsl.exe</c> session pinned into GitLoomEnv.</summary>
    public VmKeepAlive() : this(RunWslHolderOnceAsync)
    {
    }

    /// <summary>Test seam: <paramref name="runHolderOnce"/> represents one holder session — it
    /// completes when that session ends and must honor cancellation (Dispose).</summary>
    internal VmKeepAlive(
        Func<CancellationToken, Task> runHolderOnce,
        TimeSpan? backoffBase = null,
        TimeSpan? backoffCap = null,
        TimeSpan? healthyUptime = null)
    {
        _runHolderOnce = runHolderOnce ?? throw new ArgumentNullException(nameof(runHolderOnce));
        _backoffBase = backoffBase ?? DefaultBackoffBase;
        _backoffCap = backoffCap ?? DefaultBackoffCap;
        _healthyUptime = healthyUptime ?? DefaultHealthyUptime;
        _loop = Task.Run(RunAsync);
    }

    /// <summary>The holder argv (asserted by the G-12 test: scoped to the distro, no lifecycle
    /// verbs). <c>--exec</c> skips the in-distro shell — no login/profile side effects.</summary>
    public static IReadOnlyList<string> HolderArguments() =>
        new[] { "-d", WslCommands.DistroName, "--exec", "sleep", "infinity" };

    private async Task RunAsync()
    {
        var attempt = 0;
        while (!_cts.IsCancellationRequested)
        {
            var startedAt = DateTimeOffset.UtcNow;
            try
            {
                await _runHolderOnce(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch (Exception)
            {
                // A start failure (WSL not installed yet, distro missing) is an instant exit —
                // backed off below, never fatal: the holder's whole job is to outlast bad moments.
            }

            if (_cts.IsCancellationRequested)
            {
                break;
            }

            // A session that lived a while means the distro was genuinely up — reacquire promptly.
            // Rapid exits (nothing to hold yet) walk the backoff up to the cap.
            attempt = DateTimeOffset.UtcNow - startedAt >= _healthyUptime ? 0 : attempt + 1;
            try
            {
                await Task.Delay(Backoff(attempt), _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponent = Math.Min(attempt, 10);
        var delayMs = Math.Min(_backoffCap.TotalMilliseconds, _backoffBase.TotalMilliseconds * Math.Pow(2, exponent));
        return TimeSpan.FromMilliseconds(delayMs);
    }

    /// <summary>One real holder session: starts the hidden wsl.exe and completes when it exits;
    /// cancellation (Dispose) kills it so the app never leaves an orphaned session behind.</summary>
    private static async Task RunWslHolderOnceAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in HolderArguments())
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = Process.Start(psi);
        if (process is null)
        {
            return; // treated as an instant exit by the loop's backoff
        }

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch (Exception)
            {
                // Already gone — nothing to kill.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception)
        {
            // A holder that ignored cancellation must not block app shutdown.
        }

        _cts.Dispose();
    }
}
