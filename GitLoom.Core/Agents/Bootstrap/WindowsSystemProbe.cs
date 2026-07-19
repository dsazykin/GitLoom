using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Mainguard.Git;
namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// The real Windows-side <see cref="ISystemProbe"/> for P2-21 diagnostics. Kept behind the interface
/// so <see cref="SystemDiagnostics"/> stays unit-tested cross-platform with fakes; this concrete impl
/// runs only on the Windows install matrix. Virtualization flags come from WMI
/// (<c>Win32_ComputerSystem.HypervisorPresent</c>) via a PowerShell CIM query so no Windows-only
/// package reference is needed to compile.
///
/// <para>Relocated to <c>GitLoom.Core</c> in P2-48 so the shipped in-app OOBE wizard and the P2-21
/// console driver share ONE implementation (the "no divergent second implementation" rule). Every
/// child process it launches is windowless (<see cref="ProcessStartInfo.CreateNoWindow"/> +
/// <see cref="ProcessWindowStyle.Hidden"/>) — no console flashes anywhere in the OOBE flow.</para>
/// </summary>
public sealed class WindowsSystemProbe : ISystemProbe
{
    public Architecture OsArchitecture => RuntimeInformation.OSArchitecture;

    public OsBuildInfo GetOsBuild()
    {
        var isWindows = OperatingSystem.IsWindows();
        var v = Environment.OSVersion.Version;
        return new OsBuildInfo(isWindows, v.Major, v.Build);
    }

    public VirtualizationInfo GetVirtualization()
    {
        if (!OperatingSystem.IsWindows())
            return new VirtualizationInfo(false, false);

        // Win32_ComputerSystem.HypervisorPresent is true when the hypervisor (Hyper-V/WSL2 platform)
        // is running; a cold machine with virtualization simply disabled in firmware reports false, and
        // the diagnostics message routes the user to BIOS/UEFI.
        var hypervisor = QueryBool(
            "(Get-CimInstance Win32_ComputerSystem).HypervisorPresent");
        // VirtualizationFirmwareEnabled reflects the firmware VT-x/AMD-V flag independent of Hyper-V.
        var firmware = QueryBool(
            "(Get-CimInstance Win32_Processor | Select-Object -First 1).VirtualizationFirmwareEnabled");
        return new VirtualizationInfo(hypervisor, firmware);
    }

    public bool IsUserAdministrator()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true; // not the install matrix; the OS check already fails actionably there.
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var adminSid = new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null);
            // Groups enumerates DENY-ONLY entries too, so a UAC-filtered (unelevated) admin still
            // reports true — the question is "can this user elevate as themselves", never "is this
            // process elevated" (the OOBE is deliberately unelevated).
            if (identity.Groups is { } groups)
            {
                foreach (var group in groups)
                {
                    if (group.Equals(adminSid))
                    {
                        return true;
                    }
                }
            }

            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // A probe fault must never hard-stop setup on its own — the elevation step surfaces
            // reality with its own actionable failure if the account truly cannot elevate.
            return true;
        }
    }

    public long GetFreeDiskBytes()
    {
        // Environment.SystemDirectory (not GetFolderPath — the GitLoomPaths guard test bans it):
        // same drive-root answer, no exists-check semantics to trip over.
        var systemDir = Environment.SystemDirectory;
        var systemRoot = (systemDir.Length > 0 ? Path.GetPathRoot(systemDir) : null)
            ?? Path.GetPathRoot(Environment.CurrentDirectory)
            ?? "C:\\";
        try
        {
            return new DriveInfo(systemRoot).AvailableFreeSpace;
        }
        catch
        {
            return 0;
        }
    }

    private static bool QueryBool(string cimExpression)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(cimExpression);

            using var p = Process.Start(psi);
            if (p is null)
                return false;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            // Bounded: a wedged PowerShell/WMI must not hang diagnostics forever (the probe has no
            // cancellation path of its own — the audit-flagged unbounded WaitForExit).
            if (!p.WaitForExit(milliseconds: 20_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already exiting */ }
                return false;
            }

            return outputTask.GetAwaiter().GetResult().Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>The real WSL status probe: runs <c>wsl --status</c> + <c>wsl --version</c> through the
/// hardened <see cref="IWslRunner"/> and classifies with the pure <see cref="WslStatusParser"/>.</summary>
public sealed class WslStatusProbe : IWslStatusProbe
{
    private readonly IWslRunner _wsl;

    public WslStatusProbe(IWslRunner wsl) => _wsl = wsl;

    public async Task<WslStatusReport> QueryAsync(CancellationToken ct)
    {
        var status = await _wsl.RunAsync(new[] { "--status" }, stdin: null, ct).ConfigureAwait(false);
        var version = await _wsl.RunAsync(new[] { "--version" }, stdin: null, ct).ConfigureAwait(false);
        return WslStatusParser.Parse(status.StdOut, version.StdOut, status.ExitCode);
    }
}

/// <summary>
/// Daemon health probe backed by <c>wsl.exe</c>: checks the <c>gitloomd</c> process is up inside the
/// GitLoomEnv distro. Relocated to Core in P2-48 so both the console OOBE driver and the shipped app's
/// launch-routing <see cref="ProvisioningProbe"/> share it. The App also has a richer gRPC-backed
/// <see cref="IDaemonHealthProbe"/> via <c>DaemonClient</c>; this one needs no daemon connection.
/// </summary>
public sealed class WslDaemonHealthProbe : IDaemonHealthProbe, IDaemonHealthDiagnostics, IDaemonStableHealthWaiter
{
    private readonly IWslRunner _wsl;
    public WslDaemonHealthProbe(IWslRunner wsl) => _wsl = wsl;

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            // -x (exact comm match), NOT -f: -f matches any cmdline containing "gitloomd" — e.g. a
            // concurrent `journalctl -u gitloomd` — and can report healthy against a dead daemon.
            // The apphost is renamed to `gitloomd`, so the comm matches exactly (audit fix #10).
            var result = await _wsl.RunAsync(
                WslCommands.InDistro("pgrep", "-x", "gitloomd"), stdin: null, ct).ConfigureAwait(false);
            return result.Succeeded && !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch
        {
            // wsl.exe absent / distro not registered → not healthy (drives OOBE, never a crash).
            return false;
        }
    }

    /// <summary>
    /// The whole stable-health wait in ONE <c>wsl.exe</c> spawn: the consecutive-healthy loop runs as
    /// a single <c>bash -c</c> INSIDE the distro. Host-side per-second polling would spawn a fresh
    /// wsl.exe per attempt (up to ~30 in 30s) right after GitLoomEnv boots — the same spawn-burst
    /// pattern that drove the WSL service into <c>Wsl/Service/E_UNEXPECTED</c> on the Docker wait.
    /// <c>pgrep -x</c> — the apphost is renamed so the process comm is exactly <c>gitloomd</c>.
    /// </summary>
    public async Task<bool> WaitForStableHealthyAsync(int attempts, int requiredConsecutive, CancellationToken ct)
    {
        // All interpolated values are our own integers — no user input reaches this script.
        var script =
            "ok=0; " +
            $"for i in $(seq 1 {attempts}); do " +
            "if pgrep -x gitloomd >/dev/null 2>&1; then " +
            $"ok=$((ok+1)); if [ $ok -ge {requiredConsecutive} ]; then exit 0; fi; " +
            "else ok=0; fi; " +
            "sleep 1; " +
            "done; exit 1";
        try
        {
            var result = await _wsl.RunAsync(
                WslCommands.InDistro("bash", "-c", script), stdin: null, ct).ConfigureAwait(false);
            return result.Succeeded;
        }
        catch
        {
            return false; // wsl.exe absent / distro gone → not healthy, never a crash
        }
    }

    /// <summary>Gathers the daemon's systemd unit state plus its most recent journal lines, so a failed
    /// health check names the daemon's ACTUAL failure (e.g. a crash-loop's abort line) instead of the
    /// dead-end "did not report healthy". Best-effort: returns <c>null</c> when nothing can be read.</summary>
    public async Task<string?> DescribeUnhealthyAsync(CancellationToken ct)
    {
        try
        {
            var state = await _wsl.RunAsync(
                WslCommands.InDistroAsRoot("systemctl", "is-active", "gitloomd"), stdin: null, ct).ConfigureAwait(false);
            // Read a dozen lines, not just the tail-tip: for a crash-looping daemon the interesting
            // line (the exception/abort) sits a few lines ABOVE systemd's "Main process exited /
            // Scheduled restart" noise, and it is the line that makes the error card actionable.
            var journal = await _wsl.RunAsync(
                WslCommands.InDistroAsRoot("journalctl", "-u", "gitloomd", "--no-pager", "-n", "12", "-o", "cat"),
                stdin: null, ct).ConfigureAwait(false);

            var unitState = state.StdOut.Trim() is { Length: > 0 } s ? s : "unknown";
            var lines = journal.StdOut
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .ToArray();
            // Prefer lines that look like the actual failure; fall back to the raw tail.
            var interesting = lines.Where(l =>
                    l.Contains("Exception", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("error", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("fatal", StringComparison.OrdinalIgnoreCase)
                    || l.Contains("abort", StringComparison.OrdinalIgnoreCase)
                    || l.StartsWith("at ", StringComparison.Ordinal))
                .TakeLast(4)
                .ToArray();
            var tail = interesting.Length > 0 ? interesting : lines.TakeLast(3).ToArray();

            var description = $"The gitloomd service inside {WslCommands.DistroName} is '{unitState}'.";
            if (tail.Length > 0)
                description += $" Recent log: {string.Join(" | ", tail)}";
            return description.Length > 600 ? description[..600] + "…" : description;
        }
        catch
        {
            return null; // diagnosis must never turn a health failure into a crash
        }
    }
}
