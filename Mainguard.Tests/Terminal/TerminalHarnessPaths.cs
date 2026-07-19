using System;
using System.Collections.Generic;
using System.IO;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// Resolves P2-04 fixture paths in the source tree (not the build output) and reads the shrink-only
/// allowlist. Fixtures live beside the tests so the golden/byte files are the checked-in artifacts
/// replayed verbatim — no build-time copy, no EOL rewrite of the binary <c>.bytes</c> files.
/// </summary>
public static class TerminalHarnessPaths
{
    /// <summary>The repo root (the directory holding <c>GitLoom.slnx</c>).</summary>
    public static string RepoRoot
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            {
                dir = Directory.GetParent(dir)?.FullName;
            }

            return dir ?? AppContext.BaseDirectory;
        }
    }

    /// <summary><c>Mainguard.Tests/Terminal/</c> — harness sources + allowlist.</summary>
    public static string TerminalDir => Path.Combine(RepoRoot, "Mainguard.Tests", "Terminal");

    /// <summary><c>Mainguard.Tests/Transcripts/</c> — recorded <c>.bytes</c> + committed <c>.golden</c>.</summary>
    public static string TranscriptsDir => Path.Combine(RepoRoot, "Mainguard.Tests", "Transcripts");

    /// <summary>The checked-in known-failures allowlist file.</summary>
    public static string AllowlistFile => Path.Combine(TerminalDir, "known-failures.txt");

    /// <summary>True when goldens/bytes should be (re)written rather than asserted.</summary>
    public static bool RegenGoldens =>
        Environment.GetEnvironmentVariable("GITLOOM_REGEN_GOLDENS") == "1";

    /// <summary>True when the transcript recorder entry point should run (records real programs).</summary>
    public static bool RecordTranscripts =>
        Environment.GetEnvironmentVariable("GITLOOM_RECORD_TRANSCRIPTS") == "1";

    /// <summary>
    /// The set of case-ids the interim engine is allowed to fail. Parses one id per line, ignoring
    /// blank lines and <c>#</c> comments (whole-line or trailing).
    /// </summary>
    public static IReadOnlySet<string> LoadAllowlist()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(AllowlistFile))
        {
            var line = raw;
            var hash = line.IndexOf('#');
            if (hash >= 0)
            {
                line = line[..hash];
            }

            line = line.Trim();
            if (line.Length > 0)
            {
                ids.Add(line);
            }
        }

        return ids;
    }
}
