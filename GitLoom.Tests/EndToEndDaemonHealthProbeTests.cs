using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.App.Services;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Audit fix #9: the OOBE health gate must prove the WINDOWS CLIENT can complete an authenticated
/// gRPC call — a live in-VM process alone previously let setup report Done on a machine where the
/// control center could never talk to the daemon (the token bridge bug).
/// </summary>
public sealed class EndToEndDaemonHealthProbeTests
{
    // Scripts the in-VM leg: `pgrep -x gitloomd` (IsHealthy) and the single-spawn stable wait.
    private sealed class ScriptedWsl : IWslRunner
    {
        public bool ProcessUp { get; set; } = true;
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            return Task.FromResult(ProcessUp
                ? new WslRunResult(0, "4242\n", "")
                : new WslRunResult(1, "", ""));
        }
    }

    private static Task NoDelay(TimeSpan _, CancellationToken __) => Task.CompletedTask;

    [Fact]
    public async Task ProcessUpAndTransportGreen_IsHealthy()
    {
        var probe = new EndToEndDaemonHealthProbe(
            new ScriptedWsl(),
            transport: _ => Task.FromResult(new DaemonTransportHealth(true)),
            delay: NoDelay);

        Assert.True(await probe.IsHealthyAsync(CancellationToken.None));
        Assert.Null(await probe.DescribeUnhealthyAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ProcessUpButTransportDead_IsUnhealthy_AndNamesTheTransportCause()
    {
        // Exactly the shipped bug: gitloomd alive in the VM, but the client cannot authenticate.
        var probe = new EndToEndDaemonHealthProbe(
            new ScriptedWsl(),
            transport: _ => Task.FromResult(new DaemonTransportHealth(false, "no session token was found (probed: X)")),
            delay: NoDelay);

        Assert.False(await probe.IsHealthyAsync(CancellationToken.None));

        var why = await probe.DescribeUnhealthyAsync(CancellationToken.None);
        Assert.NotNull(why);
        Assert.Contains("no session token", why, StringComparison.Ordinal);
        Assert.Contains("running inside GitLoomEnv", why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessDown_TransportNeverProbed()
    {
        var transportProbed = false;
        var probe = new EndToEndDaemonHealthProbe(
            new ScriptedWsl { ProcessUp = false },
            transport: _ => { transportProbed = true; return Task.FromResult(new DaemonTransportHealth(true)); },
            delay: NoDelay);

        Assert.False(await probe.IsHealthyAsync(CancellationToken.None));
        Assert.False(transportProbed);
    }

    [Fact]
    public async Task StableWait_RetriesTransportBriefly_ThenSucceeds()
    {
        // A just-started daemon can beat the client by a beat — the stable wait must absorb that.
        var attempts = 0;
        var probe = new EndToEndDaemonHealthProbe(
            new ScriptedWsl(),
            transport: _ => Task.FromResult(++attempts >= 3
                ? new DaemonTransportHealth(true)
                : new DaemonTransportHealth(false, "port not up yet")),
            delay: NoDelay);

        Assert.True(await probe.WaitForStableHealthyAsync(attempts: 10, requiredConsecutive: 2, CancellationToken.None));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task StableWait_TransportNeverComesUp_Fails_WithCauseRetained()
    {
        var probe = new EndToEndDaemonHealthProbe(
            new ScriptedWsl(),
            transport: _ => Task.FromResult(new DaemonTransportHealth(false, "127.0.0.1:5250 was unreachable")),
            delay: NoDelay);

        Assert.False(await probe.WaitForStableHealthyAsync(attempts: 10, requiredConsecutive: 2, CancellationToken.None));

        var why = await probe.DescribeUnhealthyAsync(CancellationToken.None);
        Assert.NotNull(why);
        Assert.Contains("unreachable", why, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InVmLeg_UsesExactCommMatch_NotSubstringMatch()
    {
        // Audit fix #10 pinned: pgrep -x (comm match), never -f (cmdline substring) — -f matched a
        // concurrent `journalctl -u gitloomd` and reported healthy against a dead daemon.
        var wsl = new ScriptedWsl();
        var probe = new EndToEndDaemonHealthProbe(
            wsl,
            transport: _ => Task.FromResult(new DaemonTransportHealth(true)),
            delay: NoDelay);

        await probe.IsHealthyAsync(CancellationToken.None);

        var pgrep = wsl.Calls.Single(c => c.Contains("pgrep"));
        Assert.Contains("-x", pgrep);
        Assert.DoesNotContain("-f", pgrep);
    }
}
