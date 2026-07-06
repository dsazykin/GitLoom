using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GitLoom.Core.Safety;

/// <summary>Tunable thresholds for a scan. Defaults mirror the T-30 contract.</summary>
public sealed class PreCommitScanOptions
{
    /// <summary>A staged file larger than this (bytes) is flagged LargeFile. Default 5 MB.</summary>
    public long MaxFileBytes { get; init; } = 5L * 1024 * 1024;

    /// <summary>Staging more than this many files raises a ManyFiles warning. Default 100.</summary>
    public int ManyFilesThreshold { get; init; } = 100;

    /// <summary>Optional, off by default: flag leftover TODO/FIXME/DEBUG markers as Info.</summary>
    public bool DetectDebugLeftovers { get; init; } = false;
}

/// <summary>
/// The pure heart of the T-30 pre-commit scanner (no IO, no git, no Avalonia). Given the staged
/// entries as plain tuples, it produces the ordered finding list — so the whole detection surface is
/// unit-pinned. A finding's <see cref="PreCommitFinding.Message"/> is NEVER built from a matched
/// secret value.
/// </summary>
public static class PreCommitScanEngine
{
    // git conflict markers, only at the start of a line. `=======` is required to be exactly seven
    // equals on its own line to avoid matching Markdown/RST underlines and diff docs.
    private static readonly Regex MarkerStart = new(@"^<{7}(?:\s|$)", RegexOptions.CultureInvariant);
    private static readonly Regex MarkerMid = new(@"^={7}\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex MarkerEnd = new(@"^>{7}(?:\s|$)", RegexOptions.CultureInvariant);

    private static readonly Regex DebugLeftover =
        new(@"\b(?:TODO|FIXME|XXX|HACK|DEBUGGER|console\.log|System\.out\.println)\b",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scan the staged entries. Each entry is (path, decoded text content, isBinary, sizeBytes).
    /// Binary entries are flagged by size only and never scanned as text. The result is ordered
    /// deterministically: severity (blocker first), then path, then line, then rule.
    /// </summary>
    public static IReadOnlyList<PreCommitFinding> Scan(
        IEnumerable<(string Path, string Content, bool IsBinary, long SizeBytes)> entries,
        PreCommitScanOptions? options = null)
    {
        options ??= new PreCommitScanOptions();
        var list = entries as IReadOnlyList<(string Path, string Content, bool IsBinary, long SizeBytes)>
                   ?? entries.ToList();
        var findings = new List<PreCommitFinding>();

        foreach (var e in list)
        {
            // LargeFile — size only, applies to binary and text alike.
            if (e.SizeBytes > options.MaxFileBytes)
            {
                findings.Add(new PreCommitFinding
                {
                    Kind = FindingKind.LargeFile,
                    Severity = FindingSeverity.Warning,
                    Path = e.Path,
                    Rule = "large-file",
                    Message = $"Large file ({FormatSize(e.SizeBytes)} exceeds {FormatSize(options.MaxFileBytes)} limit)",
                });
            }

            // Never scan a binary blob as text (secrets/markers are meaningless there).
            if (e.IsBinary || string.IsNullOrEmpty(e.Content)) continue;

            var lines = SplitLines(e.Content);
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                int lineNo = i + 1;

                if (MarkerStart.IsMatch(line) || MarkerMid.IsMatch(line) || MarkerEnd.IsMatch(line))
                {
                    findings.Add(new PreCommitFinding
                    {
                        Kind = FindingKind.MergeMarker,
                        Severity = FindingSeverity.Blocker,
                        Path = e.Path,
                        Line = lineNo,
                        Rule = "merge-marker",
                        Message = "Unresolved merge conflict marker",
                    });
                }

                foreach (var pattern in SecretPatterns.All)
                {
                    if (pattern.IsMatch(line))
                    {
                        // Message uses the rule name + location ONLY — never the matched value.
                        findings.Add(new PreCommitFinding
                        {
                            Kind = FindingKind.Secret,
                            Severity = FindingSeverity.Blocker,
                            Path = e.Path,
                            Line = lineNo,
                            Rule = pattern.Rule,
                            Message = $"Possible {pattern.DisplayName} committed",
                        });
                    }
                }

                if (options.DetectDebugLeftovers && DebugLeftover.IsMatch(line))
                {
                    findings.Add(new PreCommitFinding
                    {
                        Kind = FindingKind.DebugLeftover,
                        Severity = FindingSeverity.Info,
                        Path = e.Path,
                        Line = lineNo,
                        Rule = "debug-leftover",
                        Message = "Leftover debug / TODO marker",
                    });
                }
            }
        }

        if (list.Count > options.ManyFilesThreshold)
        {
            findings.Add(new PreCommitFinding
            {
                Kind = FindingKind.ManyFiles,
                Severity = FindingSeverity.Warning,
                Path = "",
                Rule = "many-files",
                Message = $"{list.Count} files staged (over {options.ManyFilesThreshold})",
            });
        }

        return findings
            .OrderBy(f => SeverityRank(f.Severity))
            .ThenBy(f => f.Path, StringComparer.Ordinal)
            .ThenBy(f => f.Line ?? 0)
            .ThenBy(f => f.Rule, StringComparer.Ordinal)
            .ToList();
    }

    // Blocker first, then Warning, then Info.
    private static int SeverityRank(FindingSeverity s) => s switch
    {
        FindingSeverity.Blocker => 0,
        FindingSeverity.Warning => 1,
        _ => 2,
    };

    // Split on \n and strip a trailing \r so CRLF content scans identically to LF.
    private static List<string> SplitLines(string content)
    {
        var parts = content.Split('\n');
        var lines = new List<string>(parts.Length);
        foreach (var p in parts)
        {
            lines.Add(p.EndsWith('\r') ? p[..^1] : p);
        }
        return lines;
    }

    private static string FormatSize(long bytes)
    {
        const long mb = 1024 * 1024;
        const long kb = 1024;
        if (bytes >= mb) return (bytes / (double)mb).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        if (bytes >= kb) return (bytes / (double)kb).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        return bytes.ToString(CultureInfo.InvariantCulture) + " B";
    }
}
