using System;
using System.Collections.Generic;
using System.Linq;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// Pure INI merge for <c>%UserProfile%\.wslconfig</c> (P2-05). Given the existing file content and
/// the keys GitLoom wants under <c>[wsl2]</c>, returns the merged content. This class performs
/// <b>no IO</b> — the caller owns reading the file, writing the timestamped backup, and writing the
/// result.
/// </summary>
/// <remarks>
/// Invariants (binding, plan §3.2 / edge-case row 1):
/// <list type="bullet">
///   <item>Every other section, key, comment (<c>#</c>/<c>;</c>) and blank line is preserved
///   <b>byte-for-byte</b>; only <c>[wsl2]</c> gains our missing keys.</item>
///   <item>An existing user value <b>wins</b> — a key we want that is already set under
///   <c>[wsl2]</c> is left untouched (only unset keys are added).</item>
///   <item>If <c>[wsl2]</c> is absent it is created at the end of the file.</item>
///   <item>The file's existing newline style (CRLF vs LF) is preserved.</item>
/// </list>
/// The merge is idempotent: merging its own output again is a no-op.
/// </remarks>
public static class WslConfigMerger
{
    /// <summary>
    /// Returns the merged <c>.wslconfig</c> content. Only adds the provided keys under
    /// <c>[wsl2]</c> when they are not already present; all other content is preserved verbatim.
    /// </summary>
    /// <param name="existingContent">The current file content, or <c>null</c>/empty for a new file.</param>
    /// <param name="wsl2Keys">The keys GitLoom wants under <c>[wsl2]</c> (e.g. <c>memory</c>,
    /// <c>autoMemoryReclaim</c>).</param>
    public static string Merge(string? existingContent, IReadOnlyDictionary<string, string> wsl2Keys)
    {
        ArgumentNullException.ThrowIfNull(wsl2Keys);

        var content = existingContent ?? string.Empty;
        // Preserve the file's newline convention. Empty/LF-only files use LF (deterministic across
        // the Windows and Linux test legs — never Environment.NewLine).
        var newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        // Split into physical lines. A trailing newline yields a final empty element which we track
        // so the reconstruction re-emits (or omits) the trailing newline byte-for-byte.
        var lines = SplitKeepingStructure(content, out var endedWithNewline);

        // Locate the [wsl2] section's line span (header index .. exclusive end at the next header or EOF).
        int headerIndex = -1;
        int sectionEnd = -1;
        int currentHeaderIndex = -1;
        string? currentSection = null;
        for (int i = 0; i < lines.Count; i++)
        {
            var section = TryParseSectionHeader(lines[i]);
            if (section != null)
            {
                if (string.Equals(currentSection, "wsl2", StringComparison.OrdinalIgnoreCase) && sectionEnd < 0)
                    sectionEnd = i; // first header after [wsl2] closes it
                currentSection = section;
                currentHeaderIndex = i;
                if (string.Equals(section, "wsl2", StringComparison.OrdinalIgnoreCase) && headerIndex < 0)
                    headerIndex = i;
            }
        }
        if (headerIndex >= 0 && sectionEnd < 0)
            sectionEnd = lines.Count; // [wsl2] runs to EOF
        _ = currentHeaderIndex;

        // Determine which of our keys are already set under [wsl2] (user value wins).
        var alreadySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (headerIndex >= 0)
        {
            for (int i = headerIndex + 1; i < sectionEnd; i++)
            {
                var key = TryParseKey(lines[i]);
                if (key != null)
                    alreadySet.Add(key);
            }
        }

        var missing = wsl2Keys
            .Where(kv => !alreadySet.Contains(kv.Key))
            .Select(kv => $"{kv.Key}={kv.Value}")
            .ToList();

        if (missing.Count == 0)
            return content; // nothing to add — byte-for-byte unchanged (idempotency).

        if (headerIndex >= 0)
        {
            // Insert missing keys at the end of the existing [wsl2] section body, after the last
            // non-blank line so we don't push our keys below the user's trailing blank lines.
            int insertAt = sectionEnd;
            while (insertAt - 1 > headerIndex && lines[insertAt - 1].Trim().Length == 0)
                insertAt--;
            lines.InsertRange(insertAt, missing);
            return Join(lines, newline, endedWithNewline);
        }

        // No [wsl2] section: create it at the end, preserving all existing bytes.
        var appended = new List<string>(lines);

        // Ensure the existing content is newline-terminated before we append a new section, so the
        // section header starts on its own line without swallowing the user's last line.
        if (appended.Count > 0 && !endedWithNewline && !(appended.Count == 1 && appended[0].Length == 0))
            endedWithNewline = true;

        // Drop a single leading empty element for a truly empty file so we don't emit a blank first line.
        if (appended.Count == 1 && appended[0].Length == 0)
            appended.Clear();

        appended.Add("[wsl2]");
        appended.AddRange(missing);
        return Join(appended, newline, endedWithNewline: true);
    }

    // Splits content into physical lines (without their line terminators) and reports whether the
    // content ended with a newline, so Join can reproduce the exact trailing-byte structure.
    private static List<string> SplitKeepingStructure(string content, out bool endedWithNewline)
    {
        if (content.Length == 0)
        {
            endedWithNewline = false;
            return new List<string> { string.Empty };
        }

        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        endedWithNewline = normalized.EndsWith('\n');
        var parts = normalized.Split('\n').ToList();
        // A trailing newline produces a final empty element — drop it; endedWithNewline records it.
        if (endedWithNewline && parts.Count > 0 && parts[^1].Length == 0)
            parts.RemoveAt(parts.Count - 1);
        return parts;
    }

    private static string Join(IReadOnlyList<string> lines, string newline, bool endedWithNewline)
    {
        var body = string.Join(newline, lines);
        return endedWithNewline ? body + newline : body;
    }

    // Returns the section name for a header line like "[wsl2]" (trimmed), else null. Preserves the
    // user's surrounding whitespace semantics only for detection — the original line is never mutated.
    private static string? TryParseSectionHeader(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            return trimmed[1..^1].Trim();
        return null;
    }

    // Returns the key of a "key = value" line, else null for comments (# / ;), blanks, and headers.
    private static string? TryParseKey(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#' || trimmed[0] == ';' || trimmed[0] == '[')
            return null;
        var eq = trimmed.IndexOf('=');
        if (eq <= 0)
            return null;
        return trimmed[..eq].Trim();
    }
}
