using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>Relaunches the elevated helper. Behind an interface so the OOBE flow's "exactly one UAC
/// relaunch, at Construct Sandbox only" property is testable and the real <c>runas</c> is Windows-only.</summary>
public interface IElevationLauncher
{
    /// <summary>Launches the elevated helper (UAC prompt), waits for it, and returns its result.</summary>
    Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct);
}

/// <summary>
/// The real Windows elevation launcher: starts <c>GitLoom.Installer.Elevated</c> with the
/// <c>runas</c> verb (the single UAC prompt), passing the resume-target exe path so the helper can
/// register the resume Scheduled Task, and reads back the JSON result file the helper writes. This is
/// the only place the OOBE crosses the elevation boundary.
///
/// <para>Relocated to <c>GitLoom.Core</c> in P2-48 so the shipped in-app OOBE wizard and the P2-21
/// console driver share ONE launcher. The helper process is launched hidden
/// (<see cref="ProcessWindowStyle.Hidden"/> + <see cref="ProcessStartInfo.CreateNoWindow"/>) so no
/// console window flashes; the UAC consent dialog itself is shown by Windows and carries the helper's
/// branded <c>FileDescription</c> ("GitLoom Setup").</para>
/// </summary>
public sealed class RunAsElevationLauncher : IElevationLauncher
{
    private readonly string _helperExePath;
    private readonly string _resumeTargetExePath;
    private readonly string _resultPath;

    public RunAsElevationLauncher(string helperExePath, string resumeTargetExePath, string resultPath)
    {
        _helperExePath = helperExePath;
        _resumeTargetExePath = resumeTargetExePath;
        _resultPath = resultPath;
    }

    public async Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct)
    {
        // Resolve the helper from the running app's own directory. Fall back to that directory if the
        // caller handed us a bare/relative name, so a stray working directory can't hide the co-located
        // helper. Fail with a clear message (not the opaque "cannot find the file specified"
        // Win32Exception) if it is genuinely missing — this is the exact P2-21 hand-off failure point.
        var helperExe = _helperExePath;
        if (!File.Exists(helperExe))
        {
            var colocated = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(helperExe));
            if (File.Exists(colocated))
                helperExe = colocated;
        }

        if (!File.Exists(helperExe))
            throw new FileNotFoundException(
                $"The elevated helper 'GitLoom.Installer.Elevated.exe' was not found next to the app " +
                $"(looked at '{_helperExePath}'). The packaged build must co-locate it with the GitLoom " +
                $"executable; reinstall or rebuild GitLoom.",
                helperExe);

        // UseShellExecute + Verb=runas is what raises the single UAC prompt. Arguments can't be an
        // ArgumentList with ShellExecute, so they are a carefully quoted string (no user-supplied input).
        // WindowStyle=Hidden + CreateNoWindow keep any transient window off-screen — the only UI the user
        // sees is the Windows UAC consent dialog itself.
        var psi = new ProcessStartInfo
        {
            FileName = helperExe,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--resume-target \"{_resumeTargetExePath}\" --result \"{_resultPath}\"",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch the elevated helper.");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);

        if (!File.Exists(_resultPath))
            throw new InvalidOperationException(
                $"The elevated helper exited {p.ExitCode} without writing a result file.");

        return ElevatedHelperResult.Deserialize(File.ReadAllText(_resultPath));
    }
}
