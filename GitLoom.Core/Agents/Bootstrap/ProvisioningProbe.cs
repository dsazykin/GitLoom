using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Launch-routing seam (P2-48): answers the single question the app asks at startup — "is the GitLoom
/// runtime already provisioned?" — so one entry point can route to either the in-app OOBE wizard
/// (not provisioned) or straight into the control center (provisioned). Behind an interface so the
/// routing decision is unit-testable with a fake, and the real WSL/daemon checks stay Windows-only.
/// </summary>
public interface IProvisioningProbe
{
    /// <summary>True when the GitLoomOS distro is registered AND the daemon answers a health probe.
    /// Never throws — any failure (WSL absent, distro missing, daemon down) resolves to <c>false</c>,
    /// which routes to OOBE.</summary>
    Task<bool> IsProvisionedAsync(CancellationToken ct);
}

/// <summary>
/// The real provisioning probe: composes P2-21/P2-05's tested checks — the GitLoomEnv distro is
/// registered (<c>wsl --list --quiet</c>) AND the daemon is healthy (<see cref="IDaemonHealthProbe"/>).
/// This is pure composition of existing machinery; it adds NO new provisioning/OS logic (the P2-48
/// "wiring, not logic" invariant). Any exception from the underlying probes is swallowed to
/// <c>false</c> so a cold machine routes cleanly into the OOBE wizard rather than crashing at launch.
/// </summary>
public sealed class ProvisioningProbe : IProvisioningProbe
{
    private readonly IWslRunner _wsl;
    private readonly IDaemonHealthProbe _health;

    public ProvisioningProbe(IWslRunner wsl, IDaemonHealthProbe health)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
        _health = health ?? throw new ArgumentNullException(nameof(health));
    }

    public async Task<bool> IsProvisionedAsync(CancellationToken ct)
    {
        try
        {
            if (!await IsDistroRegisteredAsync(ct).ConfigureAwait(false))
                return false;

            return await _health.IsHealthyAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // WSL not installed / distro not registered / daemon unreachable → treat as not provisioned.
            return false;
        }
    }

    private async Task<bool> IsDistroRegisteredAsync(CancellationToken ct)
    {
        var list = await _wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
        if (!list.Succeeded)
            return false;

        return WslRunner.ParseDistroList(list.StdOut)
            .Any(d => string.Equals(d, WslCommands.DistroName, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The launch-routing probe the shipped app uses (audit fix #8). "Provisioned" here is a question
/// about INSTALLED STATE, not live health: the GitLoomOS distro is registered and no OOBE run is
/// mid-flight. The previous probe also demanded a live daemon answer, which forced a cold VM boot
/// inside the startup budget — an idle-stopped VM (WSL idles distros out on its own) then routed a
/// fully provisioned machine back into the OOBE wizard on a routine launch. Daemon liveness is
/// transient and already owned by the control center's reconnect/Degraded machinery; the router only
/// needs to know whether setup ever completed.
/// </summary>
public sealed class InstalledStateProbe : IProvisioningProbe
{
    private readonly IWslRunner _wsl;
    private readonly Func<OobeStage?> _persistedStage;

    /// <param name="persistedStage">The persisted OOBE stage (null = no state file). A stage other
    /// than <see cref="OobeStage.Done"/> means setup is mid-flight and owns the route — e.g. the
    /// distro can already be registered while its first boot never completed.</param>
    public InstalledStateProbe(IWslRunner wsl, Func<OobeStage?> persistedStage)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
        _persistedStage = persistedStage ?? throw new ArgumentNullException(nameof(persistedStage));
    }

    public async Task<bool> IsProvisionedAsync(CancellationToken ct)
    {
        try
        {
            var stage = _persistedStage();
            if (stage is not null && stage != OobeStage.Done)
                return false; // setup is mid-flight (possibly awaiting a reboot) — the wizard owns the route.

            var list = await _wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
            return WslRunner.ParseDistroList(list.StdOut)
                .Any(d => string.Equals(d, WslCommands.DistroName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // WSL absent / state unreadable → not provisioned; the wizard is the safe surface.
            return false;
        }
    }
}
