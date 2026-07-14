using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.Installer.Elevated;

/// <summary>
/// The P2-21 elevated helper (the single UAC boundary). It performs EXACTLY the two enumerated
/// privileged actions from <see cref="InstallerCommands.PrivilegedActionCatalog"/> —
/// <list type="number">
///   <item>Enable the two Windows optional features (<c>Enable-WindowsOptionalFeature</c>).</item>
///   <item>Register the elevated ONLOGON resume Scheduled Task (never a one-shot registry autostart,
///   which would resume unelevated).</item>
/// </list>
/// — then reports back to the unelevated OOBE via a process exit code + a JSON result file. No other
/// privileged work ever moves in here (plan §7 rejection trigger). The actual DISM/schtasks execution
/// is Windows-only and validated by the human install matrix; this file compiles everywhere.
///
/// Usage: <c>GitLoom.Installer.Elevated --resume-target &lt;oobe.exe&gt; --result &lt;result.json&gt;</c>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var (resumeTarget, resultPath) = ParseArgs(args);
        if (resumeTarget is null || resultPath is null)
        {
            Console.Error.WriteLine("usage: GitLoom.Installer.Elevated --resume-target <oobe.exe> --result <result.json>");
            return (int)ElevatedHelperExitCode.BadArguments;
        }

        // Action 1: enable the two features. The raw command was surfaced to the user before the UAC prompt.
        if (!TryEnableFeatures(out var error))
        {
            WriteResult(resultPath, new ElevatedHelperResult
            {
                FeaturesEnabled = false,
                RebootRequired = false,
                ResumeTaskRegistered = false,
                Error = error,
            });
            return (int)ElevatedHelperExitCode.FeatureEnableFailed;
        }

        // Action 2: register the elevated resume Scheduled Task (survives the reboot, self-deleting).
        if (!TryRegisterResumeTask(resumeTarget, out error))
        {
            WriteResult(resultPath, new ElevatedHelperResult
            {
                FeaturesEnabled = true,
                RebootRequired = true,
                ResumeTaskRegistered = false,
                Error = error,
            });
            return (int)ElevatedHelperExitCode.ResumeTaskRegistrationFailed;
        }

        // On a cold machine the two features always require a reboot to finish enabling — the OOBE owns
        // that reboot (we ran DISM with -NoRestart). The resume task will re-enter the OOBE afterwards.
        WriteResult(resultPath, new ElevatedHelperResult
        {
            FeaturesEnabled = true,
            RebootRequired = true,
            ResumeTaskRegistered = true,
        });
        await Task.CompletedTask;
        return (int)ElevatedHelperExitCode.Success;
    }

    private static bool TryEnableFeatures(out string? error)
    {
        // One PowerShell invocation running the exact command InstallerCommands surfaces to the user.
        var ps = InstallerCommands.EnableFeaturesPowerShell();
        return TryRun("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command", ps }, out error);
    }

    private static bool TryRegisterResumeTask(string resumeTarget, out string? error) =>
        TryRun("schtasks.exe", InstallerCommands.RegisterResumeTask(resumeTarget), out error);

    private static bool TryRun(string exe, IReadOnlyList<string> args, out string? error)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                // P2-48: windowless — powershell/schtasks must never flash a console during the
                // elevated step (the only UI the user sees is the Windows UAC consent dialog).
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null)
            {
                error = $"Could not start {exe}.";
                return false;
            }
            p.WaitForExit();
            if (p.ExitCode != 0)
            {
                error = $"{exe} exited {p.ExitCode}: {p.StandardError.ReadToEnd().Trim()}";
                return false;
            }
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // On non-Windows (this sandbox) powershell/schtasks are absent — the helper only runs on the
            // real Windows install matrix. Surface the reason honestly rather than pretend success.
            error = $"{exe} could not be run on this platform: {ex.Message}";
            return false;
        }
    }

    private static void WriteResult(string path, ElevatedHelperResult result)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, result.Serialize());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write result file '{path}': {ex.Message}");
        }
    }

    private static (string? resumeTarget, string? resultPath) ParseArgs(string[] args)
    {
        string? resumeTarget = null, resultPath = null;
        for (var i = 0; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--resume-target": resumeTarget = args[++i]; break;
                case "--result": resultPath = args[++i]; break;
            }
        }
        return (resumeTarget, resultPath);
    }
}
