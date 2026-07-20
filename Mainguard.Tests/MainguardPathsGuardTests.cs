using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Git;
using Xunit;
namespace Mainguard.Tests;

/// <summary>
/// Structural guard for the gitloomd crash-loop bug class (see <c>Mainguard.Git/MainguardPaths.cs</c>):
/// on Unix the default-option <c>Environment.GetFolderPath(...)</c> VERIFIES the directory exists and
/// returns <c>""</c> when it doesn't, so <c>Path.Combine("", "GitLoom", ...)</c> silently produces a
/// RELATIVE path that resolves against the process CWD — under systemd that was <c>/</c>, EACCES, and
/// a daemon restart loop. Every per-user path must therefore resolve through <c>MainguardPaths</c>
/// (DoNotVerify + $HOME fallback + loud failure). This test scans the shipping source and FAILS on any
/// <c>GetFolderPath</c> call outside <c>MainguardPaths.cs</c> itself, making the bug class unrepeatable
/// rather than merely fixed.
/// </summary>
public class MainguardPathsGuardTests
{
    // Built by concatenation so this file's own source never matches the scan.
    private static readonly string Needle = "GetFolder" + "Path";

    /// <summary>The source roots that ship (or gate what ships). Scratch projects excluded by design.</summary>
    private static readonly string[] ScanRoots =
    {
        "Mainguard.Agents", "Mainguard.UI", "Mainguard.Agents.UI", "Mainguard.App.Shell",
        "Mainguard.Client.App", "Mainguard.Pro.App", "Mainguard.Server", "Mainguard.Protos",
        "Mainguard.Tests", "installer",
    };

    [Fact]
    public void GetFolderPath_ShouldOnlyBeCalledInsideMainguardPaths()
    {
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var root in ScanRoots)
        {
            var dir = Path.Combine(repoRoot, root);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                // bin/obj carry generated + copied sources; MainguardPaths.cs is the one allowed caller.
                var normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/") || normalized.Contains("/obj/"))
                    continue;
                if (normalized.EndsWith("Mainguard.Git/MainguardPaths.cs", StringComparison.Ordinal))
                    continue;
                // This guard itself: its test-method name necessarily names the banned API.
                if (normalized.EndsWith("Mainguard.Tests/MainguardPathsGuardTests.cs", StringComparison.Ordinal))
                    continue;

                var lineNo = 0;
                foreach (var raw in File.ReadLines(file))
                {
                    lineNo++;
                    // Comments may (and do) legitimately NAME GetFolderPath while explaining why it
                    // must not be called — strip line comments before matching. Block comments are
                    // not used for code in this repo; a false negative there is caught in review.
                    var line = raw;
                    var comment = line.IndexOf("//", StringComparison.Ordinal);
                    if (comment >= 0)
                        line = line[..comment];

                    if (line.Contains(Needle, StringComparison.Ordinal))
                        offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{lineNo}: {raw.Trim()}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Environment." + Needle + " must only be called inside MainguardPaths (it silently returns \"\" "
            + "on Unix for a not-yet-materialized directory, producing a RELATIVE data path — the gitloomd "
            + "crash-loop bug class). Route these through MainguardPaths.DataRoot()/HomeDirectory() instead:\n"
            + string.Join("\n", offenders));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        Assert.False(dir is null, "Could not locate the repo root (Mainguard.slnx) from the test base directory.");
        return dir!;
    }
}
