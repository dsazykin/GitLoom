using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;

namespace Mainguard.Agents.UI.Services;

/// <summary>Which surface the single entry point opens on startup (P2-48 launch routing).</summary>
public enum LaunchRoute
{
    /// <summary>The runtime is provisioned — open straight into the control center (the P2-47 surface).</summary>
    ControlCenter,

    /// <summary>The runtime is not provisioned — open the in-app OOBE wizard.</summary>
    Oobe,
}

/// <summary>
/// P2-48 launch routing. The packaged GitLoom ships as one executable with one entry point and two
/// paths: on startup it probes whether the runtime is provisioned (GitLoomOS distro present + daemon
/// healthy) and routes accordingly — not provisioned → the OOBE wizard, provisioned → the control
/// center. This decider is pure over an <see cref="IProvisioningProbe"/> so the routing rule is
/// unit-tested with a fake; the real probe's WSL/daemon checks stay on the Windows matrix.
/// </summary>
public static class LaunchRouter
{
    /// <summary>Decides the launch route from a provisioning probe. A probe that never provisioned —
    /// or a probe that faults — routes to <see cref="LaunchRoute.Oobe"/> (the safe default: show setup
    /// rather than a broken control center).</summary>
    public static async Task<LaunchRoute> DecideAsync(IProvisioningProbe probe, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(probe);
        try
        {
            var provisioned = await probe.IsProvisionedAsync(ct).ConfigureAwait(false);
            return provisioned ? LaunchRoute.ControlCenter : LaunchRoute.Oobe;
        }
        catch
        {
            return LaunchRoute.Oobe;
        }
    }
}
