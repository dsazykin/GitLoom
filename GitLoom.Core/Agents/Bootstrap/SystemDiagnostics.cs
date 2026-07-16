using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>The verdict of one preflight check.</summary>
public enum DiagnosticStatus
{
    Pass,
    /// <summary>A remediable failure — carries an actionable message + doc link.</summary>
    Fail,
    /// <summary>A terminal, non-remediable gate (e.g. ARM64). Also blocks, never proceeds.</summary>
    Unsupported,
}

/// <summary>
/// One preflight check result. A <see cref="DiagnosticStatus.Fail"/> or
/// <see cref="DiagnosticStatus.Unsupported"/> MUST carry a non-empty <see cref="Message"/> and
/// <see cref="DocLink"/> (asserted by tests) so the OOBE can tell the user exactly what to do.
/// </summary>
public sealed record DiagnosticCheck(
    string Id,
    string Title,
    DiagnosticStatus Status,
    string? Message = null,
    string? DocLink = null)
{
    public bool IsBlocking => Status != DiagnosticStatus.Pass;

    public static DiagnosticCheck Pass(string id, string title) =>
        new(id, title, DiagnosticStatus.Pass);

    public static DiagnosticCheck Fail(string id, string title, string message, string docLink) =>
        new(id, title, DiagnosticStatus.Fail, message, docLink);

    public static DiagnosticCheck Unsupported(string id, string title, string message, string docLink) =>
        new(id, title, DiagnosticStatus.Unsupported, message, docLink);
}

/// <summary>The aggregated preflight report.</summary>
public sealed record DiagnosticReport(IReadOnlyList<DiagnosticCheck> Checks)
{
    /// <summary>True only when every check passed. The OOBE MUST NOT modify the system otherwise
    /// (P2-21 hard-stop invariant).</summary>
    public bool CanProceed => Checks.All(c => c.Status == DiagnosticStatus.Pass);

    /// <summary>The inverse of <see cref="CanProceed"/> — surfaced by name for the state-ordering test.</summary>
    public bool HardStop => !CanProceed;

    public IReadOnlyList<DiagnosticCheck> Failures =>
        Checks.Where(c => c.IsBlocking).ToList();
}

/// <summary>Host OS identity (build number decides Win11).</summary>
public readonly record struct OsBuildInfo(bool IsWindows, int MajorVersion, int BuildNumber);

/// <summary>Firmware/hypervisor virtualization state (from WMI + firmware flags).</summary>
public readonly record struct VirtualizationInfo(bool HypervisorPresent, bool FirmwareVirtualizationEnabled);

/// <summary>
/// The Windows-specific probe surface behind an interface so <see cref="SystemDiagnostics"/> is
/// unit-testable cross-platform with a fake. The real implementation (WMI
/// <c>Win32_ComputerSystem.HypervisorPresent</c> / firmware VT-x, <c>RtlGetVersion</c>, disk free)
/// is Windows-only and exercised by the human install matrix — never invoked from this sandbox.
/// </summary>
public interface ISystemProbe
{
    /// <summary>Process/OS architecture — drives the ARM64 unsupported gate.</summary>
    Architecture OsArchitecture { get; }

    OsBuildInfo GetOsBuild();

    VirtualizationInfo GetVirtualization();

    /// <summary>Free bytes on the volume that will host the VHDX (the system drive).</summary>
    long GetFreeDiskBytes();

    /// <summary>Whether the CURRENT user account holds administrator rights (a UAC-filtered,
    /// unelevated admin counts — the question is "can this user elevate as themselves"). Setup on a
    /// standard-user account elevates as a DIFFERENT account, which registers the resume task and the
    /// WSL distro under that other user — the audit-found wrong-account install (fix #6 gate).</summary>
    bool IsUserAdministrator();
}

/// <summary>Async seam to obtain the parsed WSL substrate state (wraps <see cref="IWslRunner"/> +
/// <see cref="WslStatusParser"/>). Split out so diagnostics need no process in a unit test.</summary>
public interface IWslStatusProbe
{
    Task<WslStatusReport> QueryAsync(CancellationToken ct);
}

/// <summary>
/// P2-21 preflight diagnostics. Runs an ordered set of independent checks — ARM64 gate, Win11 x64
/// build, WMI virtualization flags, WSL2 substrate state, ≥ 20 GB free disk — and aggregates them.
/// Every non-pass carries an actionable message + doc link. The caller (OOBE) must treat
/// <see cref="DiagnosticReport.HardStop"/> as an absolute barrier: <b>no system modification</b> runs
/// unless <see cref="DiagnosticReport.CanProceed"/>. The ARM64 gate short-circuits the whole report.
/// </summary>
public sealed class SystemDiagnostics
{
    /// <summary>The minimum free disk the payload + a provisioned repo need.</summary>
    public const long MinFreeDiskBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>Windows 11 is build 22000+; anything below is Win10 or older.</summary>
    public const int Win11MinBuild = 22000;

    // Doc anchors (stable deep links into the install guide).
    private const string DocRoot = "https://gitloom.dev/docs/install";
    public const string DocArm64 = DocRoot + "#arm64-unsupported";
    public const string DocWin11 = DocRoot + "#windows-11-required";
    public const string DocAdmin = DocRoot + "#administrator-required";
    public const string DocVirtualization = DocRoot + "#enable-virtualization";
    public const string DocWsl = DocRoot + "#enable-wsl2";
    public const string DocDisk = DocRoot + "#free-disk-space";

    private readonly ISystemProbe _probe;
    private readonly IWslStatusProbe _wsl;

    public SystemDiagnostics(ISystemProbe probe, IWslStatusProbe wsl)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _wsl = wsl ?? throw new ArgumentNullException(nameof(wsl));
    }

    public async Task<DiagnosticReport> RunAsync(CancellationToken ct)
    {
        // ARM64 gate FIRST and alone — the entire product is x64-only, so there is nothing to remediate.
        if (_probe.OsArchitecture == Architecture.Arm64)
        {
            return new DiagnosticReport(new[]
            {
                DiagnosticCheck.Unsupported(
                    "arch", "Processor architecture",
                    "GitLoom requires a 64-bit x64 (Intel/AMD) processor. This machine reports ARM64, "
                    + "which the WSL2 + Docker agent substrate does not support. GitLoom cannot be installed here.",
                    DocArm64),
            });
        }

        var checks = new List<DiagnosticCheck>
        {
            CheckWindowsBuild(),
            CheckAdministrator(),
            CheckVirtualization(),
            await CheckWslAsync(ct).ConfigureAwait(false),
            CheckDisk(),
        };
        return new DiagnosticReport(checks);
    }

    private DiagnosticCheck CheckAdministrator()
    {
        // Fix #6 gate: setup from a standard-user account elevates as a DIFFERENT (admin) account,
        // and everything elevated then lands under that other user — the resume Scheduled Task fires
        // for the wrong account and `wsl --import` registers GitLoomEnv in the ADMIN's per-user WSL,
        // invisible to the person who ran setup. Hard-stop honestly instead of half-installing.
        if (_probe.IsUserAdministrator())
            return DiagnosticCheck.Pass("admin", "Administrator account");

        return DiagnosticCheck.Fail("admin", "Administrator account",
            "GitLoom setup must run from a Windows account with administrator rights: the sandbox "
            + "construction elevates as YOUR account (one prompt), and installing from a standard "
            + "account would register the runtime under a different user. Log in as an administrator "
            + "(or have this account added to the Administrators group), then re-run setup. "
            + "Nothing has been changed on your machine.",
            DocAdmin);
    }

    private DiagnosticCheck CheckWindowsBuild()
    {
        var os = _probe.GetOsBuild();
        if (!os.IsWindows)
        {
            return DiagnosticCheck.Fail("os", "Windows 11 (x64)",
                "GitLoom's agent substrate runs on Windows 11 x64. This machine is not running Windows.",
                DocWin11);
        }
        if (os.BuildNumber < Win11MinBuild)
        {
            return DiagnosticCheck.Fail("os", "Windows 11 (x64)",
                $"Windows 11 (build {Win11MinBuild} or newer) is required; this machine reports build "
                + $"{os.BuildNumber}. Update to Windows 11, then re-run setup.",
                DocWin11);
        }
        return DiagnosticCheck.Pass("os", "Windows 11 (x64)");
    }

    private DiagnosticCheck CheckVirtualization()
    {
        var v = _probe.GetVirtualization();
        // HypervisorPresent covers the "Hyper-V/WSL2 platform already up" case; firmware VT-x covers the
        // cold machine where virtualization is simply disabled in BIOS/UEFI.
        if (v.HypervisorPresent || v.FirmwareVirtualizationEnabled)
            return DiagnosticCheck.Pass("virt", "Hardware virtualization");

        return DiagnosticCheck.Fail("virt", "Hardware virtualization",
            "Hardware virtualization is disabled in your firmware (BIOS/UEFI). Reboot into firmware "
            + "setup and enable Intel VT-x / AMD-V (often labeled \"Virtualization Technology\" or "
            + "\"SVM\"), save, and re-run setup. Nothing has been changed on your machine.",
            DocVirtualization);
    }

    private async Task<DiagnosticCheck> CheckWslAsync(CancellationToken ct)
    {
        var report = await _wsl.QueryAsync(ct).ConfigureAwait(false);
        return report.State switch
        {
            WslInstallState.Wsl2Ready => DiagnosticCheck.Pass("wsl", "WSL2 platform"),
            // These three messages must be SELF-SERVE: a non-pass hard-stops setup (the P2-21
            // no-modification-before-all-green invariant), so promising "the next step will fix it" —
            // the old text — pointed at a step the block prevents from ever running. Each names the
            // exact command that fixes the state, so "Check again" can then pass.
            WslInstallState.NotInstalled => DiagnosticCheck.Fail("wsl", "WSL2 platform",
                "The Windows Subsystem for Linux is not enabled yet. Open PowerShell as administrator, "
                + "run \"wsl --install --no-distribution\", restart Windows, then press “Re-check”.",
                DocWsl),
            WslInstallState.Wsl1Only => DiagnosticCheck.Fail("wsl", "WSL2 platform",
                "WSL is set to version 1, and GitLoom requires WSL2. Open PowerShell, run "
                + "\"wsl --set-default-version 2\", then press “Re-check”.", DocWsl),
            WslInstallState.NeedsKernelUpdate => DiagnosticCheck.Fail("wsl", "WSL2 platform",
                "WSL2 needs a kernel update before it can run the GitLoom VM. Open PowerShell, run "
                + "\"wsl --update\", then press “Re-check”.", DocWsl),
            _ => DiagnosticCheck.Fail("wsl", "WSL2 platform",
                "GitLoom could not determine the WSL state on this machine. Open a terminal and run "
                + "\"wsl --status\", then re-run setup. Nothing has been changed.", DocWsl),
        };
    }

    private DiagnosticCheck CheckDisk()
    {
        var free = _probe.GetFreeDiskBytes();
        if (free >= MinFreeDiskBytes)
            return DiagnosticCheck.Pass("disk", "Free disk space");

        var freeGb = free / (1024.0 * 1024 * 1024);
        return DiagnosticCheck.Fail("disk", "Free disk space",
            $"GitLoom needs at least 20 GB free on the system drive; only {freeGb:0.0} GB is available. "
            + "Free up space and re-run setup.",
            DocDisk);
    }
}
