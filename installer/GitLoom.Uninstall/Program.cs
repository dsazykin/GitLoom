using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.Uninstall;

/// <summary>
/// The P2-22 §J-6 uninstaller entry point. Parses the two user choices (<c>--keep-settings</c>,
/// <c>--remove-sync-remote</c>) and drives the Core <see cref="Uninstaller"/> with the real Windows
/// delegates. The ordering, failure-tolerance and G-12 distro scoping all live in Core (unit-tested);
/// this file only supplies the concrete side effects.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var options = new UninstallOptions(
            KeepSettings: args.Contains("--keep-settings"),
            RemoveSyncRemote: args.Contains("--remove-sync-remote"));

        var uninstaller = new Uninstaller(
            wsl: new WslRunner(),
            registry: new RegExeRegistryCommandRunner(),
            stopDaemon: StopDaemonAsync,
            removeScheduledTasks: RemoveScheduledTasksAsync,
            removeAppData: RemoveAppDataAsync);

        var report = await uninstaller.RunAsync(options, CancellationToken.None).ConfigureAwait(false);

        // Prove G-12: personal distros that were running before are still running after.
        var stoppedPersonal = report.PersonalDistrosBefore
            .Where(d => !report.RunningDistrosAfter.Contains(d, StringComparer.Ordinal))
            .ToArray();

        Console.WriteLine($"GitLoom uninstall {(report.Clean ? "completed" : "completed with warnings")}.");
        Console.WriteLine($"  Steps: {string.Join(" -> ", report.StepsRun)}");
        Console.WriteLine($"  GitLoomEnv unregistered: {report.DistroUnregistered}");
        if (stoppedPersonal.Length > 0)
            Console.Error.WriteLine($"  WARNING (G-12): personal distros stopped: {string.Join(", ", stoppedPersonal)}");
        foreach (var e in report.Errors)
            Console.Error.WriteLine($"  ! {e.Message}");

        return report.Clean ? 0 : 1;
    }

    private static async Task StopDaemonAsync(CancellationToken ct)
    {
        // Best-effort: ask the daemon inside the distro to stop before we terminate the distro.
        var wsl = new WslRunner();
        try { await wsl.RunAsync(WslCommands.InDistroAsRoot("pkill", "-f", "gitloomd"), null, ct).ConfigureAwait(false); }
        catch { /* distro may already be gone */ }
    }

    private static async Task RemoveScheduledTasksAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in InstallerCommands.UnregisterResumeTask())
            psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is not null) await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch { /* schtasks is Windows-only; a no-op elsewhere */ }
    }

    private static Task RemoveAppDataAsync(bool keepSettings, CancellationToken ct)
    {
        if (keepSettings) return Task.CompletedTask;
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitLoom");
        try
        {
            if (Directory.Exists(appData)) Directory.Delete(appData, recursive: true);
        }
        catch { /* leave residue rather than fail the uninstall */ }
        return Task.CompletedTask;
    }
}
