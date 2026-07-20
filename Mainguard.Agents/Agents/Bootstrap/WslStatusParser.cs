using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>
/// The WSL substrate readiness classification P2-21 diagnostics act on. Ordered from "nothing there"
/// to "ready" so a caller can treat anything below <see cref="Wsl2Ready"/> as a diagnostics
/// <c>Fail</c> that the OOBE resolves.
/// </summary>
public enum WslInstallState
{
    /// <summary>Could not classify the captured output (unrecognized dialect/locale) — treated as a
    /// soft fail so diagnostics never silently pass on a shape we don't understand.</summary>
    Unknown = 0,

    /// <summary>WSL is not installed / the optional component is not enabled (or <c>wsl.exe</c> is
    /// absent). The OOBE's enablement step must run.</summary>
    NotInstalled,

    /// <summary>WSL is present but the default version is 1 — needs <c>--set-default-version 2</c> and,
    /// on older builds, a kernel update.</summary>
    Wsl1Only,

    /// <summary>WSL2 is present but its kernel component needs updating (the classic
    /// "requires an update to its kernel component" / aka.ms/wsl2kernel message).</summary>
    NeedsKernelUpdate,

    /// <summary>WSL2 substrate ready: default version 2, kernel current. The P2-05 bootstrapper can
    /// import the distro.</summary>
    Wsl2Ready,
}

/// <summary>The parsed view of the <c>wsl --status</c> / <c>wsl --version</c> surface.</summary>
public sealed record WslStatusReport(
    WslInstallState State,
    string? DefaultVersion,
    string? WslVersion,
    string? KernelVersion)
{
    /// <summary>True only when the substrate is ready for the P2-05 import step.</summary>
    public bool IsReady => State == WslInstallState.Wsl2Ready;
}

/// <summary>
/// Pure parser (P2-21 step 1) for the <c>wsl --status</c>/<c>wsl --version</c>/feature-state outputs.
/// Takes strings already UTF-16-decoded by <see cref="WslRunner"/> (fixtures are captured as raw
/// UTF-16LE bytes and decoded the same way). Distinguishes: not installed / WSL1-only / WSL2 ready /
/// needs kernel update. Locale-tolerant where possible via structural markers, with English message
/// fallbacks. No IO.
/// </summary>
public static class WslStatusParser
{
    // "Default Version: 2" (also matches localized "Standardversion", "Version par défaut" by anchoring
    // on the trailing ": <digit>" after a line mentioning a version). We stay conservative: the numeric
    // capture is what drives classification.
    private static readonly Regex DefaultVersionRx =
        new(@"(?im)^\s*(?:Default\s+Version|Standardversion|Version\s+par\s+défaut)\s*:\s*(\d+)\s*$",
            RegexOptions.Compiled);

    private static readonly Regex WslVersionRx =
        new(@"(?im)^\s*WSL\s*version\s*:\s*([0-9][0-9.]*)\s*$", RegexOptions.Compiled);

    private static readonly Regex KernelVersionRx =
        new(@"(?im)^\s*Kernel\s*version\s*:\s*([0-9][0-9.\-]*)\s*$", RegexOptions.Compiled);

    /// <summary>Substrings (case-insensitive) that mean WSL/the optional component is not present.</summary>
    private static readonly string[] NotInstalledMarkers =
    {
        "no installed distributions",
        "is not installed",
        "not enabled",
        "optional component",
        "is not recognized",             // 'wsl' is not recognized as an internal or external command
        "wsl/install",                    // "run 'wsl --install'"
        "enable the \"windows subsystem for linux\"",
    };

    /// <summary>Substrings that mean WSL2 exists but its kernel needs updating.</summary>
    private static readonly string[] KernelUpdateMarkers =
    {
        "requires an update to its kernel component",
        "wsl2kernel",
        "kernel component",
        "update to the wsl2 linux kernel",
    };

    /// <summary>
    /// Classifies the combined <c>wsl --status</c> (+ optional <c>wsl --version</c>) output.
    /// <paramref name="exitCode"/> is the <c>wsl.exe</c> exit code; a non-zero exit with a
    /// not-installed marker is authoritative for <see cref="WslInstallState.NotInstalled"/>.
    /// </summary>
    public static WslStatusReport Parse(string? statusOutput, string? versionOutput = null, int exitCode = 0)
    {
        var status = statusOutput ?? string.Empty;
        var version = versionOutput ?? string.Empty;
        var combined = (status + "\n" + version);
        var lower = combined.ToLowerInvariant();

        var defaultVersion = Match(DefaultVersionRx, combined);
        var wslVersion = Match(WslVersionRx, combined);
        var kernelVersion = Match(KernelVersionRx, combined);

        // 1. Not installed wins first — an unenabled component can otherwise emit stray text.
        if (ContainsAny(lower, NotInstalledMarkers) && defaultVersion is null && wslVersion is null)
            return new WslStatusReport(WslInstallState.NotInstalled, null, wslVersion, kernelVersion);

        // A hard non-zero exit with no parseable version fields and no other signal → not installed.
        if (exitCode != 0 && defaultVersion is null && wslVersion is null && kernelVersion is null)
            return new WslStatusReport(WslInstallState.NotInstalled, null, null, null);

        // 2. Kernel-update message: WSL2 present but stale kernel.
        if (ContainsAny(lower, KernelUpdateMarkers))
            return new WslStatusReport(WslInstallState.NeedsKernelUpdate, defaultVersion, wslVersion, kernelVersion);

        // 3. Version-driven classification.
        if (int.TryParse(defaultVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dv))
        {
            if (dv >= 2)
                return new WslStatusReport(WslInstallState.Wsl2Ready, defaultVersion, wslVersion, kernelVersion);
            return new WslStatusReport(WslInstallState.Wsl1Only, defaultVersion, wslVersion, kernelVersion);
        }

        // 4. `wsl --version` succeeded (modern store WSL always defaults to v2) even with no
        // "Default Version" line → ready.
        if (wslVersion is not null)
            return new WslStatusReport(WslInstallState.Wsl2Ready, defaultVersion, wslVersion, kernelVersion);

        return new WslStatusReport(WslInstallState.Unknown, defaultVersion, wslVersion, kernelVersion);
    }

    private static string? Match(Regex rx, string input)
    {
        var m = rx.Match(input);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static bool ContainsAny(string haystackLower, IEnumerable<string> needles) =>
        needles.Any(n => haystackLower.Contains(n, StringComparison.Ordinal));
}
