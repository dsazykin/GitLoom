using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Step 1: guard that WSL2 itself is present. Runs <c>wsl --list --quiet</c>; a missing
/// <c>wsl.exe</c> surfaces as <see cref="WslNotInstalledException"/> from the runner during the
/// <b>check</b> phase, so the whole bootstrap fails actionably before any mutating act. This step
/// never installs WSL — enablement is P2-21's installer flow.
///
/// <para><b>Zero-distro machines (audit fix):</b> on the exact state a fresh install creates
/// (<c>wsl --install --no-distribution</c> + reboot) <c>wsl --list --quiet</c> exits NON-ZERO with a
/// "has no installed distributions" message — WSL itself is fine; there is simply nothing to list
/// yet (the import step right after this one creates the first distro). Keying purely off the exit
/// code misread that state as "WSL not installed" and killed the golden fresh-machine path, so a
/// ran-but-empty list now counts as satisfied.</para>
/// </summary>
public sealed class DetectDistroStep : IBootstrapStep
{
    private readonly IWslRunner _wsl;

    public DetectDistroStep(IWslRunner wsl) => _wsl = wsl;

    public string Name => "Detect WSL2";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        // Throws WslNotInstalledException if wsl.exe is absent — propagates as the actionable,
        // terminal failure. A running WSL answers --list with exit 0; a WSL with zero distros
        // answers non-zero but with the recognizable no-distributions message.
        var result = await _wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
        return result.Succeeded || IndicatesNoDistributions(result);
    }

    public Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        // Reached only if wsl.exe ran but --list failed for a reason other than "no distros yet"
        // (broken/partial WSL). We do NOT run `wsl --install` (P2-21 owns enablement) — surface the
        // actionable failure instead.
        throw new WslNotInstalledException();
    }

    /// <summary>
    /// True when a failed <c>--list</c> is really "WSL works, zero distros installed": the output
    /// carries the no-distributions message (localized builds keep the <c>wsl --install</c> /
    /// <c>WSL_E_DEFAULT_DISTRO_NOT_FOUND</c> markers even where the prose is translated).
    /// </summary>
    internal static bool IndicatesNoDistributions(WslRunResult result)
    {
        // Deliberately NOT matched: the generic "wsl --install" hint — it also appears in the
        // component-not-enabled error, which must stay a hard failure here.
        var combined = (result.StdOut + "\n" + result.StdErr).ToLowerInvariant();
        return combined.Contains("no installed distributions", StringComparison.Ordinal)
            || combined.Contains("wsl_e_default_distro_not_found", StringComparison.Ordinal)
            || combined.Contains("https://aka.ms/wslstore", StringComparison.Ordinal);
    }
}
