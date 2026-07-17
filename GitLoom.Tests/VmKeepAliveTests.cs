using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The GitLoomEnv keep-alive holder (field outage 2026-07-17: WSL idle-stopped the distro between
/// gRPC calls, killing gitloomd mid-flight — once mid-migration). The holder must be scoped to the
/// distro with no lifecycle verbs (G-12), restart itself when a session ends (distro re-imported /
/// not yet imported), and stop promptly and permanently on Dispose.
/// </summary>
public class VmKeepAliveTests
{
    [Fact]
    public void HolderArguments_AreDistroScoped_WithNoLifecycleVerbs()
    {
        var args = VmKeepAlive.HolderArguments();

        Assert.Contains("-d", args);
        Assert.Contains(WslCommands.DistroName, args);
        Assert.Contains("--exec", args);
        // G-12: never a VM-wide or lifecycle verb from the keep-alive.
        Assert.DoesNotContain("--shutdown", args);
        Assert.DoesNotContain("--terminate", args);
        Assert.DoesNotContain("--unregister", args);
        Assert.DoesNotContain("--import", args);
    }

    [Fact]
    public async Task HolderExit_IsRestarted_UntilDisposed()
    {
        var starts = 0;
        var restarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var keepAlive = new VmKeepAlive(
            runHolderOnce: _ =>
            {
                if (Interlocked.Increment(ref starts) >= 3)
                {
                    restarted.TrySetResult();
                }

                return Task.CompletedTask; // an instant exit — the distro isn't there to hold
            },
            backoffBase: TimeSpan.FromMilliseconds(1),
            backoffCap: TimeSpan.FromMilliseconds(5)))
        {
            var done = await Task.WhenAny(restarted.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(restarted.Task, done); // restarted at least twice after the first exit
        }

        var startsAtDispose = Volatile.Read(ref starts);
        await Task.Delay(100);
        Assert.Equal(startsAtDispose, Volatile.Read(ref starts)); // no restarts after Dispose
    }

    [Fact]
    public async Task StartFailures_AreSwallowed_AndRetried()
    {
        var starts = 0;
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var keepAlive = new VmKeepAlive(
            runHolderOnce: _ =>
            {
                if (Interlocked.Increment(ref starts) >= 2)
                {
                    retried.TrySetResult();
                }

                throw new InvalidOperationException("wsl.exe missing"); // never fatal to the loop
            },
            backoffBase: TimeSpan.FromMilliseconds(1),
            backoffCap: TimeSpan.FromMilliseconds(5));

        var done = await Task.WhenAny(retried.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(retried.Task, done);
    }

    [Fact]
    public async Task Dispose_CancelsALiveHolderSession()
    {
        var sessionStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observedCancel = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var keepAlive = new VmKeepAlive(
            runHolderOnce: async ct =>
            {
                sessionStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct); // a healthy long-lived session
                }
                catch (OperationCanceledException)
                {
                    observedCancel.TrySetResult();
                    throw;
                }
            });

        var live = await Task.WhenAny(sessionStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(sessionStarted.Task, live); // the holder is live

        keepAlive.Dispose(); // must complete promptly (bounded wait) and cancel the session

        var cancelled = await Task.WhenAny(observedCancel.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(observedCancel.Task, cancelled);
    }
}
