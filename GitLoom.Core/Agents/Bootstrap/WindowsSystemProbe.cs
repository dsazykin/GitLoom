using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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

    public long GetFreeDiskBytes()
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
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
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return output.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
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
public sealed class WslDaemonHealthProbe : IDaemonHealthProbe
{
    private readonly IWslRunner _wsl;
    public WslDaemonHealthProbe(IWslRunner wsl) => _wsl = wsl;

    public async Task<bool> IsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var result = await _wsl.RunAsync(
                WslCommands.InDistro("pgrep", "-f", "gitloomd"), stdin: null, ct).ConfigureAwait(false);
            return result.Succeeded && !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch
        {
            // wsl.exe absent / distro not registered → not healthy (drives OOBE, never a crash).
            return false;
        }
    }
}
