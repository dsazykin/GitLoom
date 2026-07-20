using System.Collections.Generic;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Pure parser for <c>git lfs ls-files</c> output (T-17). Each line is
/// <c>&lt;oid&gt; &lt;*|-&gt; &lt;path&gt;</c>: the OID first token, a single status char
/// (<c>*</c> = content present locally, <c>-</c> = pointer only), then the path. The two
/// single-space separators after the OID and the status char are fixed, and the path is the
/// remainder verbatim (it may contain spaces, so never split it on spaces). No IO — unit-testable.
/// </summary>
public static class LfsLsFilesParser
{
    public static IReadOnlyList<LfsFile> Parse(string? output)
    {
        var result = new List<LfsFile>();
        if (string.IsNullOrEmpty(output)) return result;

        foreach (var raw in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;

            var firstSpace = line.IndexOf(' ');
            // Need at least "<oid> <status> <path>": oid, space, status char, space, ≥1 path char.
            if (firstSpace <= 0 || firstSpace + 3 > line.Length) continue;

            var statusChar = line[firstSpace + 1];
            if (statusChar != '*' && statusChar != '-') continue;
            if (line[firstSpace + 2] != ' ') continue;

            var path = line.Substring(firstSpace + 3);
            if (path.Length == 0) continue;

            result.Add(new LfsFile
            {
                Oid = line.Substring(0, firstSpace),
                Path = path,
                IsDownloaded = statusChar == '*'
            });
        }
        return result;
    }
}
