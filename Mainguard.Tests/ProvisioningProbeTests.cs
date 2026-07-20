using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-48 — the real <see cref="ProvisioningProbe"/> composes P2-21/P2-05's tested checks: the MainguardEnv
/// distro is registered AND the daemon is healthy. Proven over fake WSL/health seams (no real WSL).
/// </summary>
public class ProvisioningProbeTests
{
    private sealed class FakeWslRunner : IWslRunner
    {
        private readonly WslRunResult _result;
        private readonly bool _throws;
        public FakeWslRunner(string stdout, int exit = 0) => _result = new WslRunResult(exit, stdout, "");
        public FakeWslRunner(bool throws) => _throws = throws;
        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
            => _throws ? throw new Exception("wsl.exe not found") : Task.FromResult(_result);
    }

    private sealed class FakeHealth : IDaemonHealthProbe
    {
        private readonly bool _healthy;
        public FakeHealth(bool healthy) => _healthy = healthy;
        public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(_healthy);
    }

    [Fact]
    public async Task DistroRegistered_AndDaemonHealthy_IsProvisioned()
    {
        var probe = new ProvisioningProbe(
            new FakeWslRunner($"docker-desktop\r\n{WslCommands.DistroName}\r\nUbuntu\r\n"),
            new FakeHealth(healthy: true));
        Assert.True(await probe.IsProvisionedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DistroMissing_IsNotProvisioned()
    {
        var probe = new ProvisioningProbe(
            new FakeWslRunner("Ubuntu\r\ndocker-desktop\r\n"),
            new FakeHealth(healthy: true));
        Assert.False(await probe.IsProvisionedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DistroRegistered_ButDaemonDown_IsNotProvisioned()
    {
        var probe = new ProvisioningProbe(
            new FakeWslRunner($"{WslCommands.DistroName}\r\n"),
            new FakeHealth(healthy: false));
        Assert.False(await probe.IsProvisionedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WslAbsent_IsNotProvisioned_NeverThrows()
    {
        var probe = new ProvisioningProbe(new FakeWslRunner(throws: true), new FakeHealth(healthy: true));
        Assert.False(await probe.IsProvisionedAsync(CancellationToken.None));
    }
}

/// <summary>
/// Audit fix #8 — the SHIPPED routing probe asks about installed state only (distro registered, no
/// OOBE run mid-flight). It never demands a live daemon: that forced a cold VM boot inside the
/// startup budget and routed provisioned machines (idle-stopped VM) back into the wizard.
/// </summary>
public class InstalledStateProbeTests
{
    private sealed class FakeWslRunner : IWslRunner
    {
        private readonly WslRunResult _result;
        private readonly bool _throws;
        public FakeWslRunner(string stdout, int exit = 0) => _result = new WslRunResult(exit, stdout, "");
        public FakeWslRunner(bool throws) => _throws = throws;
        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
            => _throws ? throw new Exception("wsl.exe not found") : Task.FromResult(_result);
    }

    private static readonly FakeWslRunner Registered = new($"Ubuntu\r\n{WslCommands.DistroName}\r\n");
    private static readonly FakeWslRunner NotRegistered = new("Ubuntu\r\n");

    [Theory]
    [InlineData(null)]                 // no state file: a completed install whose wizard cleared state
    [InlineData(OobeStage.Done)]       // completed install, state retained
    public async Task Registered_NoRunMidFlight_IsProvisioned_EvenWithTheVmStopped(OobeStage? stage)
    {
        // No daemon-health leg at all: an idle-stopped VM must still route to the control center.
        var probe = new InstalledStateProbe(Registered, () => stage);
        Assert.True(await probe.IsProvisionedAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(OobeStage.Diagnostics)]
    [InlineData(OobeStage.EnableFeatures)]
    [InlineData(OobeStage.RebootPending)]
    [InlineData(OobeStage.ImportVm)]
    public async Task SetupMidFlight_RoutesToOobe_EvenWhenTheDistroAlreadyExists(OobeStage stage)
    {
        // The distro can be registered while its first boot never completed — the wizard owns this.
        var probe = new InstalledStateProbe(Registered, () => stage);
        Assert.False(await probe.IsProvisionedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DistroMissing_IsNotProvisioned()
    {
        var probe = new InstalledStateProbe(NotRegistered, () => OobeStage.Done);
        Assert.False(await probe.IsProvisionedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task WslAbsent_OrStateUnreadable_IsNotProvisioned_NeverThrows()
    {
        var wslGone = new InstalledStateProbe(new FakeWslRunner(throws: true), () => OobeStage.Done);
        Assert.False(await wslGone.IsProvisionedAsync(CancellationToken.None));

        var stateBroken = new InstalledStateProbe(Registered, () => throw new InvalidOperationException("corrupt state"));
        Assert.False(await stateBroken.IsProvisionedAsync(CancellationToken.None));
    }
}
