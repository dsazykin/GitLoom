using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.Installer;

/// <summary>Relaunches the elevated helper. Behind an interface so the OOBE flow's "exactly one UAC
/// relaunch, at Construct Sandbox only" property is testable and the real <c>runas</c> is Windows-only.</summary>
public interface IElevationLauncher
{
    /// <summary>Launches the elevated helper (UAC prompt), waits for it, and returns its result.</summary>
    Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct);
}

/// <summary>
/// The real Windows elevation launcher: starts <c>GitLoom.Installer.Elevated</c> with the
/// <c>runas</c> verb (the single UAC prompt), passing the OOBE exe path so the helper can register the
/// resume Scheduled Task, and reads back the JSON result file the helper writes. This is the only place
/// the OOBE crosses the elevation boundary.
/// </summary>
public sealed class RunAsElevationLauncher : IElevationLauncher
{
    private readonly string _helperExePath;
    private readonly string _oobeExePath;
    private readonly string _resultPath;

    public RunAsElevationLauncher(string helperExePath, string oobeExePath, string resultPath)
    {
        _helperExePath = helperExePath;
        _oobeExePath = oobeExePath;
        _resultPath = resultPath;
    }

    public async Task<ElevatedHelperResult> ConstructSandboxAsync(CancellationToken ct)
    {
        // UseShellExecute + Verb=runas is what raises the single UAC prompt. Arguments can't be an
        // ArgumentList with ShellExecute, so they are a carefully quoted string (no user-supplied input).
        var psi = new ProcessStartInfo
        {
            FileName = _helperExePath,
            UseShellExecute = true,
            Verb = "runas",
            Arguments = $"--resume-target \"{_oobeExePath}\" --result \"{_resultPath}\"",
            CreateNoWindow = true,
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
