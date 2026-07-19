using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>The <c>GetDaemonInfo</c> result as the Core skew policy consumes it (proto-free —
/// Mainguard.Agents never references the gRPC stack).</summary>
/// <param name="DaemonVersion">The daemon's assembly informational version.</param>
/// <param name="PayloadVersion">The GitLoomOS payload version from <c>/etc/gitloomos-release</c>;
/// empty when the stamp is absent.</param>
public sealed record DaemonVersionInfo(string DaemonVersion, string PayloadVersion);

/// <summary>
/// The pure tier-1 version-skew decision: should the App refresh the in-VM daemon? The field
/// failure this guards against: the daemon deployed inside GitLoomEnv is the build baked into the
/// GitLoomOS tarball at install time, so every RPC the app grows later answers
/// <c>Unimplemented</c> until the daemon is refreshed.
/// </summary>
public static class DaemonUpdatePolicy
{
    /// <summary>
    /// True when the in-VM daemon should be refreshed from the app-shipped payload.
    /// <paramref name="daemonInfo"/> is the <c>GetDaemonInfo</c> answer — pass <c>null</c> when the
    /// daemon answered <c>Unimplemented</c> (a pre-<c>GetDaemonInfo</c> daemon IS the skew signal).
    /// A daemon that could not be reached at all is NOT a skew signal — never call this for
    /// daemon-down; skip instead (the reconnect machinery owns liveness).
    /// Build metadata after '+' is ignored: the versioned release train (the csproj
    /// <c>Version</c>), not the commit hash, decides skew.
    /// </summary>
    public static bool IsRefreshNeeded(string appVersion, DaemonVersionInfo? daemonInfo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);

        if (daemonInfo is null || string.IsNullOrWhiteSpace(daemonInfo.DaemonVersion))
        {
            return true; // pre-RPC daemon (Unimplemented) or a daemon that can't name itself
        }

        return !string.Equals(
            StripBuildMetadata(appVersion), StripBuildMetadata(daemonInfo.DaemonVersion),
            StringComparison.Ordinal);
    }

    /// <summary>Drops SemVer build metadata (<c>0.2.0+abc123</c> → <c>0.2.0</c>).</summary>
    public static string StripBuildMetadata(string version)
    {
        var trimmed = version.Trim();
        var plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }
}

/// <summary>
/// Pure argument-list builders for the in-place daemon refresh — the automated form of the manual
/// field fix (publish → copy over <c>/opt/gitloom</c> → rename apphost → chmod → restart unit).
/// Kept separate from the runner (like <see cref="WslCommands"/>/<see cref="VmUpgradeCommands"/>)
/// so the command shapes — and the G-12 invariant that <b>no builder ever emits the VM-wide
/// shutdown verb</b> — are unit-testable without a process. Everything is scoped in-distro to
/// <c>GitLoomEnv</c>; the swap keeps <see cref="RollbackDir"/> so a bad payload is recoverable.
/// </summary>
public static class DaemonUpdateCommands
{
    /// <summary>Where the payload installs the daemon (see build/gitloomos/README.md).</summary>
    public const string InstallDir = "/opt/gitloom";

    /// <summary>The staged copy of the new daemon, assembled before the swap.</summary>
    public const string StagingDir = "/opt/gitloom.new";

    /// <summary>The previous install, kept across the swap as the rollback.</summary>
    public const string RollbackDir = "/opt/gitloom.old";

    /// <summary>The systemd unit (and the apphost's required name — P2-05's <c>pgrep -x gitloomd</c>).</summary>
    public const string UnitName = "gitloomd";

    /// <summary>The apphost name a raw <c>dotnet publish</c> emits (renamed to <see cref="UnitName"/>;
    /// a build.sh-produced payload arrives already renamed, so the rename is probed first).</summary>
    public const string PublishedApphostName = "GitLoom.Server";

    public static IReadOnlyList<string> StopUnit() =>
        WslCommands.InDistroAsRoot("systemctl", "stop", UnitName);

    public static IReadOnlyList<string> StartUnit() =>
        WslCommands.InDistroAsRoot("systemctl", "start", UnitName);

    public static IReadOnlyList<string> RemoveStaging() =>
        WslCommands.InDistroAsRoot("rm", "-rf", StagingDir);

    public static IReadOnlyList<string> CreateStaging() =>
        WslCommands.InDistroAsRoot("mkdir", "-p", StagingDir);

    /// <summary>Copies the payload directory's CONTENTS (<c>&lt;dir&gt;/.</c>) into staging —
    /// <paramref name="vmPayloadDir"/> is the /mnt-translated form of the Windows payload dir.</summary>
    public static IReadOnlyList<string> CopyPayloadIntoStaging(string vmPayloadDir) =>
        WslCommands.InDistroAsRoot("cp", "-r", vmPayloadDir.TrimEnd('/') + "/.", StagingDir + "/");

    /// <summary>Exit 0 iff the staged payload still carries the un-renamed apphost.</summary>
    public static IReadOnlyList<string> ProbePublishedApphost() =>
        WslCommands.InDistroAsRoot("test", "-e", StagingDir + "/" + PublishedApphostName);

    /// <summary>The apphost rename (<c>GitLoom.Server</c> → <c>gitloomd</c>; it loads
    /// <c>GitLoom.Server.dll</c> by its embedded name, so the rename is transparent).</summary>
    public static IReadOnlyList<string> RenameApphost() =>
        WslCommands.InDistroAsRoot("mv", StagingDir + "/" + PublishedApphostName, StagingDir + "/" + UnitName);

    public static IReadOnlyList<string> MakeDaemonExecutable() =>
        WslCommands.InDistroAsRoot("chmod", "0755", StagingDir + "/" + UnitName);

    /// <summary>Drops the PREVIOUS refresh's rollback before this swap creates a new one.</summary>
    public static IReadOnlyList<string> RemoveRollback() =>
        WslCommands.InDistroAsRoot("rm", "-rf", RollbackDir);

    public static IReadOnlyList<string> RetireCurrent() =>
        WslCommands.InDistroAsRoot("mv", InstallDir, RollbackDir);

    public static IReadOnlyList<string> PromoteStaging() =>
        WslCommands.InDistroAsRoot("mv", StagingDir, InstallDir);

    /// <summary>The recovery move when the promote failed AFTER the retire — only ever issued while
    /// <see cref="InstallDir"/> is absent (a blind restore into an existing dir would nest it).</summary>
    public static IReadOnlyList<string> RestoreRollback() =>
        WslCommands.InDistroAsRoot("mv", RollbackDir, InstallDir);

    /// <summary>Every builder — used by the G-12 unit test to prove none emit the VM-wide shutdown
    /// verb and all stay scoped to <c>GitLoomEnv</c>.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> AllBuilders() => new[]
    {
        StopUnit(),
        StartUnit(),
        RemoveStaging(),
        CreateStaging(),
        CopyPayloadIntoStaging("/mnt/c/Program Files/GitLoom/payload/daemon"),
        ProbePublishedApphost(),
        RenameApphost(),
        MakeDaemonExecutable(),
        RemoveRollback(),
        RetireCurrent(),
        PromoteStaging(),
        RestoreRollback(),
    };
}

/// <summary>The outcome of one refresh attempt — never a bare throw at the caller.</summary>
/// <param name="Message">Human-readable outcome for the oobe.log breadcrumb.</param>
public sealed record DaemonRefreshResult(bool Succeeded, string Message);

/// <summary>The in-place daemon refresh seam (interface-first, per Core convention).</summary>
public interface IDaemonUpdater
{
    /// <summary>Refreshes the in-VM daemon from <paramref name="payloadDirectory"/> (a Windows
    /// host path; translated to its <c>/mnt/&lt;drive&gt;/…</c> form for the in-distro copy).</summary>
    Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct);
}

/// <summary>
/// Performs the tier-1 in-place daemon refresh over the <see cref="IWslRunner"/> seam — argument
/// lists only, never a shell string, never a VM-wide lifecycle verb (G-12). Sequence: stop the
/// <c>gitloomd</c> unit → stage the payload into <see cref="DaemonUpdateCommands.StagingDir"/> →
/// rename the apphost + chmod 0755 → swap dirs keeping <see cref="DaemonUpdateCommands.RollbackDir"/>
/// → start the unit. On a failure after the current install was retired, the rollback is restored;
/// the unit start is always re-attempted so a failed refresh never leaves the daemon stopped. The
/// restarted daemon writes a fresh session token, which the client re-reads per call — self-healing.
/// </summary>
public sealed class DaemonUpdater : IDaemonUpdater
{
    private readonly IWslRunner _wsl;

    public DaemonUpdater(IWslRunner wsl)
    {
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
    }

    /// <summary>Where the packaged app ships the daemon payload (the MSBuild
    /// <c>$(GitLoomDaemonPayload)</c> copy step in GitLoom.App.csproj) — mirrors how
    /// <c>payload/GitLoomOS.tar.gz</c> is resolved.</summary>
    public static string DefaultPayloadDirectory() =>
        Path.Combine(AppContext.BaseDirectory, "payload", "daemon");

    /// <summary>The in-VM (<c>/mnt/&lt;drive&gt;/…</c>) form of a Windows payload directory — the
    /// path <c>cp</c> reads inside GitLoomEnv. Pure (reuses <see cref="HostPathTranslator"/> pinned
    /// to the Linux branch; native Linux paths — tests, CI — pass through unchanged).</summary>
    public static string ToVmPath(string hostPayloadDirectory) =>
        HostPathTranslator.ToDaemonOpenablePath(hostPayloadDirectory, daemonIsWindows: false);

    public async Task<DaemonRefreshResult> RefreshAsync(string payloadDirectory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        var vmPayloadDir = ToVmPath(payloadDirectory);

        var retired = false;
        try
        {
            await RequireAsync(DaemonUpdateCommands.StopUnit(), "stop the gitloomd unit", ct).ConfigureAwait(false);
            await RequireAsync(DaemonUpdateCommands.RemoveStaging(), "clear stale staging", ct).ConfigureAwait(false);
            await RequireAsync(DaemonUpdateCommands.CreateStaging(), "create the staging dir", ct).ConfigureAwait(false);
            await RequireAsync(
                DaemonUpdateCommands.CopyPayloadIntoStaging(vmPayloadDir),
                $"stage the payload from '{vmPayloadDir}'", ct).ConfigureAwait(false);

            // A raw publish ships `GitLoom.Server`; a build.sh payload arrives already renamed.
            var apphost = await _wsl.RunAsync(DaemonUpdateCommands.ProbePublishedApphost(), stdin: null, ct)
                .ConfigureAwait(false);
            if (apphost.Succeeded)
            {
                await RequireAsync(DaemonUpdateCommands.RenameApphost(), "rename the apphost to gitloomd", ct)
                    .ConfigureAwait(false);
            }

            await RequireAsync(DaemonUpdateCommands.MakeDaemonExecutable(), "chmod the gitloomd apphost", ct)
                .ConfigureAwait(false);

            await RequireAsync(DaemonUpdateCommands.RemoveRollback(), "drop the previous rollback", ct)
                .ConfigureAwait(false);
            await RequireAsync(DaemonUpdateCommands.RetireCurrent(), "retire the current install", ct)
                .ConfigureAwait(false);
            retired = true;
            await RequireAsync(DaemonUpdateCommands.PromoteStaging(), "promote the staged install", ct)
                .ConfigureAwait(false);
            retired = false; // promoted — /opt/gitloom exists again; never blind-restore over it
            await RequireAsync(DaemonUpdateCommands.StartUnit(), "start the gitloomd unit", ct)
                .ConfigureAwait(false);

            return new DaemonRefreshResult(
                true, $"daemon refreshed from '{payloadDirectory}' (rollback kept at {DaemonUpdateCommands.RollbackDir})");
        }
        catch (DaemonRefreshStepException ex)
        {
            // Never leave the VM without /opt/gitloom or with the unit stopped. The restore is
            // only issued when the promote failed after the retire (InstallDir is absent then —
            // a blind mv into an existing dir would nest the rollback inside it).
            if (retired)
            {
                await TryRunAsync(DaemonUpdateCommands.RestoreRollback(), ct).ConfigureAwait(false);
            }

            await TryRunAsync(DaemonUpdateCommands.StartUnit(), ct).ConfigureAwait(false);
            return new DaemonRefreshResult(false, ex.Message);
        }
    }

    private async Task RequireAsync(IReadOnlyList<string> args, string what, CancellationToken ct)
    {
        var result = await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var detail = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
            throw new DaemonRefreshStepException(
                $"could not {what} (exit {result.ExitCode}): {detail.Trim()}");
        }
    }

    private async Task TryRunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            await _wsl.RunAsync(args, stdin: null, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Recovery is best-effort; the primary failure is what gets reported.
        }
    }

    /// <summary>Internal control-flow signal for a failed refresh step (never escapes
    /// <see cref="RefreshAsync"/> — the caller sees a typed <see cref="DaemonRefreshResult"/>).</summary>
    private sealed class DaemonRefreshStepException : Exception
    {
        public DaemonRefreshStepException(string message)
            : base(message)
        {
        }
    }
}

/// <summary>How one <see cref="DaemonAutoRefresh.RunAsync"/> attempt ended — the typed form of the
/// oobe.log breadcrumb, for callers (the App's startup toast) that need more than prose.</summary>
public enum DaemonRefreshOutcomeKind
{
    /// <summary>The daemon never answered within the retry budget — skipped, not an error.</summary>
    Unreachable,

    /// <summary>The daemon already matches the app; nothing was touched.</summary>
    UpToDate,

    /// <summary>Skew was detected but the app ships no daemon payload — skipped.</summary>
    SkippedNoPayload,

    /// <summary>The in-place refresh ran and succeeded — the daemon now runs the app's version.</summary>
    Refreshed,

    /// <summary>The refresh ran and failed; the daemon was left on (or restored to) the previous build.</summary>
    RefreshFailed,

    /// <summary>An unexpected fault in the flow itself (never thrown at the caller).</summary>
    Faulted,
}

/// <summary>One typed refresh outcome (the callback payload of <see cref="DaemonAutoRefresh.RunAsync"/>).</summary>
/// <param name="Kind">How the attempt ended.</param>
/// <param name="PreviousDaemonVersion">The daemon version found before any action — <c>null</c> when
/// unknown (unreachable, faulted, or a pre-<c>GetDaemonInfo</c> daemon that cannot name itself).</param>
/// <param name="NewDaemonVersion">The version the daemon runs after a successful refresh (the app's
/// version); <c>null</c> for every other kind.</param>
/// <param name="Detail">The same human-readable text the oobe.log breadcrumb carries.</param>
public sealed record DaemonRefreshOutcome(
    DaemonRefreshOutcomeKind Kind,
    string? PreviousDaemonVersion,
    string? NewDaemonVersion,
    string Detail);

/// <summary>A composed startup-toast payload (proto- and UI-free; the App binds it to its toast host).</summary>
/// <param name="Message">The one-line toast text (Voice Bible pattern T: past tense, names the object).</param>
/// <param name="IsWarning">True for the failed-refresh warning tone; false for the quiet success pill.</param>
public sealed record DaemonRefreshToastContent(string Message, bool IsWarning);

/// <summary>
/// The outcome → toast policy: only an attempt that actually CHANGED something (or tried and
/// failed) earns a toast. Up-to-date, unreachable, no-payload, and internal faults stay silent —
/// they were silent before the toast existed and a startup pill for "nothing happened" is noise.
/// Pure so the trigger rule is unit-testable without Avalonia.
/// </summary>
public static class DaemonRefreshToast
{
    public static DaemonRefreshToastContent? TryCompose(DaemonRefreshOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return outcome.Kind switch
        {
            DaemonRefreshOutcomeKind.Refreshed => new DaemonRefreshToastContent(
                $"Mainguard OS daemon updated to {outcome.NewDaemonVersion}.", IsWarning: false),
            DaemonRefreshOutcomeKind.RefreshFailed => new DaemonRefreshToastContent(
                "Daemon update didn't complete — still on "
                + $"{outcome.PreviousDaemonVersion ?? "the previous build"}. Details in oobe.log.",
                IsWarning: true),
            _ => null,
        };
    }
}

/// <summary>
/// The one call the App makes at control-center startup (fire-and-forget): query the daemon's
/// version, decide skew, refresh if needed, log the outcome — and never throw. Daemon-down is a
/// silent skip (the reconnect machinery owns liveness); a query that answered <c>Unimplemented</c>
/// (mapped to <c>null</c> by the caller) IS the skew signal for pre-<c>GetDaemonInfo</c> daemons.
/// The query is retried briefly because the launch path wakes the VM in the background and systemd
/// needs a few seconds to bring <c>gitloomd</c> up.
/// </summary>
public static class DaemonAutoRefresh
{
    /// <param name="appVersion">The App's own informational version.</param>
    /// <param name="queryDaemonInfo">Calls <c>GetDaemonInfo</c>; returns <c>null</c> for an
    /// <c>Unimplemented</c> answer; THROWS when the daemon is unreachable.</param>
    /// <param name="updater">The refresh performer (fake in tests).</param>
    /// <param name="payloadDirectory">The app-shipped daemon payload dir (Windows host path).</param>
    /// <param name="log">Outcome breadcrumbs (the App passes its oobe.log writer).</param>
    /// <param name="queryAttempts">Bounded unreachable-retry budget (the VM may still be booting).</param>
    /// <param name="queryRetryDelay">Delay between unreachable retries (default 5 s; 0 in tests).</param>
    /// <param name="onOutcome">Optional typed-outcome callback (the App's startup toast). Invoked at
    /// most once, after the outcome is logged, on the caller's thread — never on cancellation, and a
    /// throwing callback is swallowed (this flow must never ripple into the app).</param>
    public static async Task RunAsync(
        string appVersion,
        Func<CancellationToken, Task<DaemonVersionInfo?>> queryDaemonInfo,
        IDaemonUpdater updater,
        string payloadDirectory,
        Action<string> log,
        CancellationToken ct,
        int queryAttempts = 5,
        TimeSpan? queryRetryDelay = null,
        Action<DaemonRefreshOutcome>? onOutcome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        ArgumentNullException.ThrowIfNull(queryDaemonInfo);
        ArgumentNullException.ThrowIfNull(updater);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        ArgumentNullException.ThrowIfNull(log);

        var delay = queryRetryDelay ?? TimeSpan.FromSeconds(5);

        // Log first, then report — and never let a throwing callback masquerade as a flow fault.
        void Report(DaemonRefreshOutcomeKind kind, string? previous, string? updatedTo, string detail)
        {
            try
            {
                onOutcome?.Invoke(new DaemonRefreshOutcome(kind, previous, updatedTo, detail));
            }
            catch (Exception)
            {
                // The outcome consumer is cosmetic (a toast); its failure never ripples back.
            }
        }

        try
        {
            DaemonVersionInfo? info = null;
            var reached = false;
            for (var attempt = 0; attempt < queryAttempts && !reached; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }

                try
                {
                    info = await queryDaemonInfo(ct).ConfigureAwait(false);
                    reached = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception)
                {
                    // Daemon down / VM still booting — retry within the bounded budget.
                }
            }

            if (!reached)
            {
                const string skipped =
                    "daemon auto-update: daemon unreachable — skipped (the reconnect machinery owns liveness)";
                log(skipped);
                Report(DaemonRefreshOutcomeKind.Unreachable, previous: null, updatedTo: null, skipped);
                return;
            }

            if (!DaemonUpdatePolicy.IsRefreshNeeded(appVersion, info))
            {
                var upToDate = $"daemon auto-update: daemon {info!.DaemonVersion} matches app {appVersion} — up to date"
                    + (info.PayloadVersion.Length > 0 ? $" (payload {info.PayloadVersion})" : "");
                log(upToDate);
                Report(DaemonRefreshOutcomeKind.UpToDate, info.DaemonVersion, updatedTo: null, upToDate);
                return;
            }

            // null previous == the daemon could not name itself (pre-GetDaemonInfo).
            var previousVersion = info?.DaemonVersion is { Length: > 0 } v ? v : null;
            var daemonName = previousVersion ?? "pre-GetDaemonInfo";
            if (!Directory.Exists(payloadDirectory)
                || !Directory.EnumerateFileSystemEntries(payloadDirectory).Any())
            {
                var noPayload = $"daemon auto-update: skew detected (daemon {daemonName}, app {appVersion}) but no "
                    + $"daemon payload at '{payloadDirectory}' — skipped";
                log(noPayload);
                Report(DaemonRefreshOutcomeKind.SkippedNoPayload, previousVersion, updatedTo: null, noPayload);
                return;
            }

            log($"daemon auto-update: refreshing skewed daemon ({daemonName} → {appVersion})");
            var result = await updater.RefreshAsync(payloadDirectory, ct).ConfigureAwait(false);
            if (result.Succeeded)
            {
                log($"daemon auto-update: {result.Message}");
                Report(DaemonRefreshOutcomeKind.Refreshed, previousVersion,
                    DaemonUpdatePolicy.StripBuildMetadata(appVersion), result.Message);
            }
            else
            {
                log($"daemon auto-update FAILED (daemon left on the previous build): {result.Message}");
                Report(DaemonRefreshOutcomeKind.RefreshFailed, previousVersion, updatedTo: null, result.Message);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // App shutdown mid-refresh-decision — nothing to log (and no outcome: nobody is listening).
        }
        catch (Exception ex)
        {
            // A failed update must never crash (or even ripple into) the app.
            log($"daemon auto-update FAILED: {ex.Message}");
            Report(DaemonRefreshOutcomeKind.Faulted, previous: null, updatedTo: null, ex.Message);
        }
    }
}
