using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace GitLoom.Tests.TestTools.ScriptedAgent;

/// <summary>
/// The ScriptedAgentHarness (TI-P2-00 §A.4.2). A cross-platform console binary that
/// stands in for a real coding CLI: it speaks the P2-09 control protocol and runs a
/// scripted timeline so every Phase-2 orchestration behavior (yield, keep-alive, queue
/// transitions, plan approval, kill switch, repair loop) is driven deterministically.
///
/// Control protocol (line-oriented, over stdout/stdin):
///   <list type="bullet">
///     <item>On a <c>yield</c> step the harness writes <see cref="UpdateRequested"/> to
///       stdout and blocks until it reads a line containing <see cref="UpdateReady"/>
///       on stdin (unless <c>--ignore-yields</c> is set — the timeout path — where it
///       announces the request but does not wait).</item>
///   </list>
///
/// CLI: <c>--script "&lt;step&gt;;&lt;step&gt;;..."</c> [<c>--cwd &lt;dir&gt;</c>]
///      [<c>--ignore-yields</c>]. Steps:
///   <c>write:&lt;relpath&gt;:&lt;content&gt;</c>,
///   <c>commit:&lt;relpath&gt;:&lt;content&gt;:&lt;msg&gt;</c>,
///   <c>emit:&lt;text&gt;</c>, <c>yield</c>, <c>hang</c>, <c>crash</c>, <c>exit:&lt;code&gt;</c>.
/// </summary>
public static class HarnessEntry
{
    public const string UpdateRequested = "[IPC_UPDATE_REQUESTED]";
    public const string UpdateReady = "[IPC_UPDATE_READY]";

    public static int Main(string[] args)
    {
        string script = string.Empty;
        string cwd = Directory.GetCurrentDirectory();
        bool ignoreYields = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--script" when i + 1 < args.Length:
                    script = args[++i];
                    break;
                case "--cwd" when i + 1 < args.Length:
                    cwd = args[++i];
                    break;
                case "--ignore-yields":
                    ignoreYields = true;
                    break;
            }
        }

        var stdout = Console.Out;
        foreach (var raw in script.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var step = raw.Trim();
            if (step.Length == 0)
            {
                continue;
            }

            var verb = step.Split(':', 2)[0];
            switch (verb)
            {
                case "emit":
                    stdout.WriteLine(Arg(step, 1));
                    stdout.Flush();
                    break;

                case "write":
                    {
                        var parts = step.Split(':', 3);
                        Write(cwd, parts[1], parts.Length > 2 ? parts[2] : string.Empty);
                        break;
                    }

                case "commit":
                    {
                        var parts = step.Split(':', 4);
                        Write(cwd, parts[1], parts.Length > 2 ? parts[2] : string.Empty);
                        Git(cwd, "add", parts[1]);
                        Git(cwd, "commit", "-m", parts.Length > 3 ? parts[3] : "scripted commit");
                        break;
                    }

                case "yield":
                    stdout.WriteLine(UpdateRequested);
                    stdout.Flush();
                    if (!ignoreYields)
                    {
                        WaitForReady();
                    }

                    break;

                case "hang":
                    Thread.Sleep(Timeout.Infinite);
                    break;

                case "crash":
                    // Non-graceful termination for crash-resume tests.
                    Environment.Exit(139);
                    break;

                case "exit":
                    return int.TryParse(Arg(step, 1), out var code) ? code : 0;
            }
        }

        return 0;
    }

    private static string Arg(string step, int index)
    {
        var parts = step.Split(':', 2);
        return index < parts.Length ? parts[index] : string.Empty;
    }

    private static void WaitForReady()
    {
        string? line;
        while ((line = Console.In.ReadLine()) != null)
        {
            if (line.Contains(UpdateReady, StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    private static void Write(string cwd, string relPath, string content)
    {
        var full = Path.Combine(cwd, relPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(full, content);
    }

    private static void Git(string cwd, params string[] arguments)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        using var process = Process.Start(psi);
        process?.WaitForExit();
    }
}
