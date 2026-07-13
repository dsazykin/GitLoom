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
/// </summary>
public sealed class DetectDistroStep : IBootstrapStep
{
    private readonly IWslRunner _wsl;

    public DetectDistroStep(IWslRunner wsl) => _wsl = wsl;

    public string Name => "Detect WSL2";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        // Throws WslNotInstalledException if wsl.exe is absent — propagates as the actionable,
        // terminal failure. A running WSL answers --list with exit 0.
        var result = await _wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
        return result.Succeeded;
    }

    public Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        // Reached only if wsl.exe ran but --list failed (broken/partial WSL). We do NOT run
        // `wsl --install` (P2-21 owns enablement) — surface the actionable failure instead.
        throw new WslNotInstalledException();
    }
}
