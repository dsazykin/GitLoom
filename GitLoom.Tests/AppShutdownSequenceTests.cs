using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The Core shutdown orchestrator over a fake environment: order, the StopVmOnExit on/off legs, and
/// the reentrancy guard (a second exit request must not double-run the teardown). Tray-hide never
/// reaching here is a property of the App's interception, asserted in the ViewModel/App layer, not
/// this pure sequence.
/// </summary>
public class AppShutdownSequenceTests
{
    private sealed class Recorder : IProgress<string>
    {
        public readonly List<string> Lines = new();

        public void Report(string value) => Lines.Add(value);
    }

    private sealed class FakeEnv : IAppShutdownEnvironment
    {
        public readonly List<string> Calls = new();
        public bool StopVmOnExitValue;
        public int ReleaseCount;
        public int StopCount;

        public bool StopVmOnExit => StopVmOnExitValue;

        public void ReleaseKeepAlive()
        {
            Calls.Add("Release");
            ReleaseCount++;
        }

        public Task StopVmAsync(CancellationToken ct)
        {
            Calls.Add("StopVm");
            StopCount++;
            return Task.CompletedTask;
        }

        public void Log(string message) => Calls.Add($"log:{message}");
    }

    [Fact]
    public async Task StopVmOnExit_on_releases_then_stops_in_order()
    {
        var env = new FakeEnv { StopVmOnExitValue = true };
        var rec = new Recorder();

        await new AppShutdownSequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(new[]
        {
            ShutdownStatus.ReleasingKeepAlive,
            ShutdownStatus.StoppingVm,
            ShutdownStatus.Done,
        }, rec.Lines);
        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(1, env.StopCount);
        Assert.True(env.Calls.IndexOf("Release") < env.Calls.IndexOf("StopVm"),
            "keep-alive must be released before the VM stop");
    }

    [Fact]
    public async Task StopVmOnExit_off_releases_but_never_stops_the_vm()
    {
        var env = new FakeEnv { StopVmOnExitValue = false };
        var rec = new Recorder();

        await new AppShutdownSequence(env).RunAsync(rec, CancellationToken.None);

        Assert.Equal(new[]
        {
            ShutdownStatus.ReleasingKeepAlive,
            ShutdownStatus.Done,
        }, rec.Lines);
        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(0, env.StopCount);
    }

    [Fact]
    public async Task Second_exit_request_is_reentrancy_guarded_and_does_not_double_run()
    {
        var env = new FakeEnv { StopVmOnExitValue = true };
        var seq = new AppShutdownSequence(env);

        await seq.RunAsync(null, CancellationToken.None);
        Assert.True(seq.HasRun);

        await seq.RunAsync(null, CancellationToken.None); // second request

        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(1, env.StopCount);
    }

    [Fact]
    public async Task Concurrent_exit_requests_run_the_teardown_once()
    {
        var env = new FakeEnv { StopVmOnExitValue = true };
        var seq = new AppShutdownSequence(env);

        await Task.WhenAll(
            Task.Run(() => seq.RunAsync(null, CancellationToken.None)),
            Task.Run(() => seq.RunAsync(null, CancellationToken.None)));

        Assert.Equal(1, env.ReleaseCount);
        Assert.Equal(1, env.StopCount);
    }
}
