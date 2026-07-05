using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure unified-diff parser/serializer (T-06). No repo, no IO. <see cref="Serialize"/> round-trips
/// <see cref="Parse"/> byte-identically for LF input, which is what makes <see cref="PatchBuilder"/>'s
/// verbatim hunk emission safe to feed to <c>git apply</c>.
/// </summary>
public static class PatchParser
{
    // @@ -oldStart[,oldCount] +newStart[,newCount] @@<section heading>
    private static readonly Regex HunkHeaderRegex =
        new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@(.*)$", RegexOptions.Compiled);

    public static IReadOnlyList<FilePatch> Parse(string unifiedDiff)
    {
        var result = new List<FilePatch>();
        if (string.IsNullOrEmpty(unifiedDiff)) return result;

        var lines = SplitLines(unifiedDiff);
        int n = lines.Count;

        // File sections are delimited by "diff --git " lines.
        var starts = new List<int>();
        for (int i = 0; i < n; i++)
            if (lines[i].StartsWith("diff --git ")) starts.Add(i);

        if (starts.Count == 0)
        {
            // No file header (bare hunk stream) — treat the whole input as one section.
            result.Add(ParseSection(lines, 0, n));
            return result;
        }

        // Anything before the first "diff --git " is preamble and ignored.
        for (int s = 0; s < starts.Count; s++)
        {
            int start = starts[s];
            int end = (s + 1 < starts.Count) ? starts[s + 1] : n;
            result.Add(ParseSection(lines, start, end));
        }
        return result;
    }

    public static string Serialize(FilePatch patch)
    {
        var sb = new StringBuilder();
        sb.Append(patch.Header); // stored verbatim, including its trailing newline(s)

        foreach (var hunk in patch.Hunks)
        {
            sb.Append("@@ -").Append(hunk.OldStart);
            if (!hunk.OldCountOmitted) sb.Append(',').Append(hunk.OldCount);
            sb.Append(" +").Append(hunk.NewStart);
            if (!hunk.NewCountOmitted) sb.Append(',').Append(hunk.NewCount);
            sb.Append(" @@").Append(hunk.SectionHeading).Append('\n');

            foreach (var line in hunk.Lines)
            {
                sb.Append(PrefixOf(line.Kind)).Append(line.Text).Append('\n');
                if (line.NoNewlineAtEof) sb.Append("\\ No newline at end of file\n");
            }
        }
        return sb.ToString();
    }

    private static FilePatch ParseSection(IReadOnlyList<string> lines, int start, int end)
    {
        var header = new StringBuilder();
        int i = start;
        while (i < end && !HunkHeaderRegex.IsMatch(lines[i]))
        {
            header.Append(lines[i]).Append('\n');
            i++;
        }

        var hunks = new List<DiffHunk>();
        while (i < end && HunkHeaderRegex.IsMatch(lines[i]))
        {
            var headerLine = lines[i];
            i++;

            var body = new List<DiffLine>();
            while (i < end && !HunkHeaderRegex.IsMatch(lines[i]) && !lines[i].StartsWith("diff --git "))
            {
                var line = lines[i];
                i++;

                if (line.StartsWith("\\"))
                {
                    // "\ No newline at end of file" flags the immediately preceding line.
                    if (body.Count > 0)
                    {
                        var prev = body[^1];
                        body[^1] = new DiffLine { Kind = prev.Kind, Text = prev.Text, NoNewlineAtEof = true };
                    }
                    continue;
                }

                DiffLineKind? kind = line.Length == 0
                    ? DiffLineKind.Context
                    : line[0] switch
                    {
                        ' ' => DiffLineKind.Context,
                        '+' => DiffLineKind.Add,
                        '-' => DiffLineKind.Delete,
                        _ => null
                    };
                if (kind == null) continue; // unknown line — skip defensively

                body.Add(new DiffLine { Kind = kind.Value, Text = line.Length == 0 ? "" : line.Substring(1) });
            }

            hunks.Add(BuildHunk(headerLine, body));
        }

        return new FilePatch { Header = header.ToString(), Hunks = hunks };
    }

    private static DiffHunk BuildHunk(string headerLine, IReadOnlyList<DiffLine> body)
    {
        var m = HunkHeaderRegex.Match(headerLine);
        bool oldOmitted = !m.Groups[2].Success;
        bool newOmitted = !m.Groups[4].Success;
        return new DiffHunk
        {
            OldStart = int.Parse(m.Groups[1].Value),
            OldCount = oldOmitted ? 1 : int.Parse(m.Groups[2].Value),
            NewStart = int.Parse(m.Groups[3].Value),
            NewCount = newOmitted ? 1 : int.Parse(m.Groups[4].Value),
            SectionHeading = m.Groups[5].Value,
            Lines = body,
            OldCountOmitted = oldOmitted,
            NewCountOmitted = newOmitted
        };
    }

    private static char PrefixOf(DiffLineKind kind) => kind switch
    {
        DiffLineKind.Add => '+',
        DiffLineKind.Delete => '-',
        _ => ' '
    };

    private static List<string> SplitLines(string text)
    {
        var raw = text.Split('\n');
        var list = new List<string>(raw);
        // Drop the trailing empty element produced by the final newline (patches are
        // newline-terminated); each remaining element is one physical line, sans '\n'.
        if (list.Count > 0 && list[^1].Length == 0) list.RemoveAt(list.Count - 1);
        return list;
    }
}
