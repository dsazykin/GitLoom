using System;
using System.Collections.Generic;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Pure "line history" filter (T-12 line-history v1). Given a line range and the adjacent-version
/// unified diff for each <see cref="FileVersion"/>, keeps only the versions whose patch touches the
/// range — the versions that changed those lines. Reuses the T-06 <see cref="PatchParser"/> for the
/// hunk geometry; no repo, no IO.
///
/// <para><b>Approximation.</b> This is a v1 approximation of <c>git log -L &lt;start&gt;,&lt;end&gt;:file</c>.
/// The range is matched against each commit's diff in a fixed coordinate space (it does not walk the
/// range backwards through earlier edits, nor follow content across moves the way <c>-L</c> does), so
/// it can over- or under-report on files that shifted heavily. It is a fast, dependency-free heuristic,
/// not exact rename/move line tracking.</para>
/// </summary>
public static class LineHistoryFilter
{
    /// <summary>
    /// True if any hunk in <paramref name="unifiedDiff"/> overlaps the inclusive, 1-based line range
    /// [<paramref name="startLine"/>, <paramref name="endLine"/>] on either the old or the new side.
    /// Checking both sides catches pure deletions (old-side only) as well as adds/edits (new side).
    /// </summary>
    public static bool PatchIntersectsRange(string unifiedDiff, int startLine, int endLine)
    {
        var (lo, hi) = Normalize(startLine, endLine);
        foreach (var file in PatchParser.Parse(unifiedDiff))
        {
            foreach (var hunk in file.Hunks)
            {
                if (HunkIntersects(hunk, lo, hi)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True if <paramref name="hunk"/>'s old-side or new-side span overlaps the inclusive range
    /// [<paramref name="startLine"/>, <paramref name="endLine"/>] (1-based).
    /// </summary>
    public static bool HunkIntersects(DiffHunk hunk, int startLine, int endLine)
    {
        var (lo, hi) = Normalize(startLine, endLine);

        // git omits a ",1" count in the header; a zero count means an empty side (pure add/delete)
        // which is anchored just after OldStart/NewStart, so treat it as a single-line span there.
        int oldCount = hunk.OldCountOmitted ? 1 : Math.Max(hunk.OldCount, 1);
        int newCount = hunk.NewCountOmitted ? 1 : Math.Max(hunk.NewCount, 1);

        bool oldHit = Overlaps(hunk.OldStart, hunk.OldStart + oldCount - 1, lo, hi);
        bool newHit = Overlaps(hunk.NewStart, hunk.NewStart + newCount - 1, lo, hi);
        return oldHit || newHit;
    }

    /// <summary>
    /// Filters <paramref name="history"/> to the versions whose adjacent-version diff (supplied by
    /// <paramref name="patchFor"/>) touches the range. <paramref name="patchFor"/> returns the unified
    /// diff of a version against its predecessor; a null/empty result (e.g. the introducing commit has
    /// no predecessor diff) is treated as "no intersection" and dropped.
    /// </summary>
    public static IReadOnlyList<FileVersion> FilterByLineRange(
        IReadOnlyList<FileVersion> history, int startLine, int endLine, Func<FileVersion, string?> patchFor)
    {
        var kept = new List<FileVersion>();
        foreach (var version in history)
        {
            var patch = patchFor(version);
            if (!string.IsNullOrEmpty(patch) && PatchIntersectsRange(patch, startLine, endLine))
            {
                kept.Add(version);
            }
        }
        return kept;
    }

    private static (int Lo, int Hi) Normalize(int a, int b) => a <= b ? (a, b) : (b, a);

    private static bool Overlaps(int start, int end, int lo, int hi) => start <= hi && lo <= end;
}
