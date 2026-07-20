using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// Step 0 — the Phase-4 re-register migration. If a pre-rebrand <c>GitLoomEnv</c> distro is present and
/// the new <c>MainguardEnv</c> is not, re-register the existing install under the new name: export the
/// legacy rootfs, import it as <c>MainguardEnv</c>, then unregister <c>GitLoomEnv</c> — so an upgrading
/// user keeps their warm VM (repos, adapters) under the renamed distro. All three verbs are scoped to
/// the two NAMED distros via <see cref="WslCommands"/>; the VM-wide shutdown verb is never emitted (G-12).
///
/// <para><b>Best-effort, never blocking.</b> A fresh install has no legacy distro, so this is a no-op and
/// <see cref="ImportDistroStep"/> provisions <c>MainguardEnv</c> directly. On an upgrade a failed
/// export/import is LOGGED and swallowed — the step still reports satisfied, so the chain falls through to
/// a clean fresh provision instead of hard-stopping setup. The one-shot <see cref="_attempted"/> guard is
/// what lets the bootstrapper's post-act re-check pass even when the migration could not complete.</para>
///
/// <para><b>Scope — this moves the distro NAME only.</b> The re-imported rootfs still carries the
/// pre-rebrand in-VM layout (<c>/home/gitloom</c>, the <c>gitloomd</c> unit); the daemon auto-refresh
/// (<see cref="DaemonUpdater"/>) and VM-upgrade machinery reconcile the in-VM identity separately. This
/// path cannot be exercised without a real WSL install — it is covered by unit tests over the command
/// shapes and the scripted <see cref="IWslRunner"/>, not an end-to-end distro migration.</para>
/// </summary>
public sealed class MigrateLegacyDistroStep : IBootstrapStep
{
    private readonly IWslRunner _wsl;
    private readonly BootstrapOptions _options;
    private bool _attempted;

    public MigrateLegacyDistroStep(IWslRunner wsl, BootstrapOptions options)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string Name => "Migrate legacy environment";

    public async Task<bool> IsSatisfiedAsync(CancellationToken ct)
    {
        // One-shot: once an attempt has run (success OR a logged failure), the migration is "done" as far
        // as the chain is concerned — a hard failure must never re-check-fail this best-effort step into a
        // BootstrapException that blocks setup; the fresh-provision fallback below handles the rest.
        if (_attempted)
            return true;

        var distros = await ListDistrosAsync(ct).ConfigureAwait(false);
        // Satisfied when there is nothing to re-register: no legacy distro, or the new one already exists.
        return !Contains(distros, WslCommands.LegacyDistroName) || Contains(distros, _options.DistroName);
    }

    public async Task ExecuteAsync(IProgress<string> log, CancellationToken ct)
    {
        _attempted = true;

        // A prior partial migration can leave BOTH distros registered. The new one wins — just retire the
        // legacy leftover rather than exporting over a good install.
        var distros = await ListDistrosAsync(ct).ConfigureAwait(false);
        if (Contains(distros, _options.DistroName))
        {
            log.Report($"{_options.DistroName} already present — retiring legacy {WslCommands.LegacyDistroName}.");
            await BestEffortAsync(WslCommands.UnregisterLegacy(), ct).ConfigureAwait(false);
            return;
        }

        var exportPath = Path.Combine(Path.GetTempPath(), $"mainguard-legacy-export-{Guid.NewGuid():N}.tar");
        try
        {
            log.Report($"Migrating {WslCommands.LegacyDistroName} → {_options.DistroName} (exporting existing environment)…");
            var export = await _wsl.RunAsync(WslCommands.ExportLegacy(exportPath), stdin: null, ct).ConfigureAwait(false);
            if (!export.Succeeded)
            {
                // Nothing was changed — leave the legacy distro in place; a fresh MainguardEnv provisions next.
                log.Report($"Could not export {WslCommands.LegacyDistroName} (wsl --export exit {export.ExitCode}); "
                    + $"leaving it in place — a fresh {_options.DistroName} will be provisioned. {Tail(export)}");
                return;
            }

            log.Report($"Importing {_options.DistroName} from the migrated environment…");
            var import = await _wsl.RunAsync(
                WslCommands.ImportMigrated(_options.InstallDir, exportPath), stdin: null, ct).ConfigureAwait(false);
            if (!import.Succeeded)
            {
                // A half-import must not shadow the clean fresh provision — drop it before falling through.
                await BestEffortAsync(WslCommands.Unregister(), ct).ConfigureAwait(false);
                log.Report($"Could not import the migrated {_options.DistroName} (wsl --import exit {import.ExitCode}); "
                    + $"a fresh {_options.DistroName} will be provisioned instead. {Tail(import)}");
                return;
            }

            log.Report($"Re-registered as {_options.DistroName}; unregistering legacy {WslCommands.LegacyDistroName}.");
            await BestEffortAsync(WslCommands.UnregisterLegacy(), ct).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(exportPath)) File.Delete(exportPath); }
            catch { /* temp export cleanup is best-effort */ }
        }
    }

    private async Task<IReadOnlyList<string>> ListDistrosAsync(CancellationToken ct)
    {
        var result = await _wsl.RunAsync(WslCommands.ListQuiet(), stdin: null, ct).ConfigureAwait(false);
        return WslRunner.ParseDistroList(result.StdOut);
    }

    private async Task BestEffortAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try { await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false); }
        catch { /* best-effort cleanup — the migration outcome is already logged */ }
    }

    private static bool Contains(IReadOnlyList<string> distros, string name) =>
        distros.Any(d => string.Equals(d, name, StringComparison.OrdinalIgnoreCase));

    private static string Tail(WslRunResult r)
    {
        var detail = new[] { r.StdErr, r.StdOut }
            .Select(s => s?.Trim())
            .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        return string.IsNullOrEmpty(detail) ? string.Empty : detail!;
    }
}
