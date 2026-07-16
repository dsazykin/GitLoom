using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-48 — the real <see cref="ProvisioningProbe"/> composes P2-21/P2-05's tested checks: the GitLoomEnv
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
