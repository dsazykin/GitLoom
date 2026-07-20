using System;
using System.Diagnostics;
using System.IO;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// Test-only git CLI helper for the P2-06 integration tests (running commands inside agent
/// worktrees, scripting agent pushes, and driving the Windows-side fetch/merge round-trip).
/// Not a production runner — the daemon services under test route through the shared
/// <c>GitService.RunGit</c> primitive.
/// </summary>
internal static class AgentTestGit
{
    /// <summary>A disposable temp VM-root directory for a test (holds repos/ and worktrees/).</summary>
    internal static string NewVmRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "gitloom-vmroot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static (int Code, string Out, string Err) Run(string workDir, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>Runs git and throws if it fails — for test setup steps that must succeed.</summary>
    internal static string RunChecked(string workDir, params string[] args)
    {
        var (code, output, err) = Run(workDir, args);
        if (code != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed ({code}): {err}");
        }

        return output;
    }

    /// <summary>Sets a committer identity in a worktree so agent commits succeed.</summary>
    internal static void SetIdentity(string workDir)
    {
        RunChecked(workDir, "config", "user.name", "agent-a1");
        RunChecked(workDir, "config", "user.email", "agent@gitloom.local");
    }

    internal static void DeleteTree(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Never fail a test from cleanup.
        }
    }
}
