using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.UI.Services;
using Mainguard.App.Shell.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-48 launch-routing proof: the single entry point routes to the control center when the runtime is
/// provisioned and to the OOBE wizard when it isn't — decided over a faked <see cref="IProvisioningProbe"/>
/// so the rule is proven without WSL/daemon. Also proves the safe-default: a faulting probe → OOBE.
/// </summary>
public class LaunchRoutingTests
{
    private sealed class FakeProbe : IProvisioningProbe
    {
        private readonly bool _provisioned;
        private readonly bool _throws;
        public FakeProbe(bool provisioned, bool throws = false)
        {
            _provisioned = provisioned;
            _throws = throws;
        }
        public Task<bool> IsProvisionedAsync(CancellationToken ct)
            => _throws ? throw new InvalidOperationException("wsl.exe not found")
                       : Task.FromResult(_provisioned);
    }

    [Fact]
    public async Task Provisioned_RoutesToControlCenter()
    {
        var route = await LaunchRouter.DecideAsync(new FakeProbe(provisioned: true), CancellationToken.None);
        Assert.Equal(LaunchRoute.ControlCenter, route);
    }

    [Fact]
    public async Task NotProvisioned_RoutesToOobe()
    {
        var route = await LaunchRouter.DecideAsync(new FakeProbe(provisioned: false), CancellationToken.None);
        Assert.Equal(LaunchRoute.Oobe, route);
    }

    [Fact]
    public async Task ProbeThatFaults_FallsBackToOobe()
    {
        // A cold machine where wsl.exe is absent must never crash the launch — it shows setup.
        var route = await LaunchRouter.DecideAsync(new FakeProbe(provisioned: false, throws: true), CancellationToken.None);
        Assert.Equal(LaunchRoute.Oobe, route);
    }
}
