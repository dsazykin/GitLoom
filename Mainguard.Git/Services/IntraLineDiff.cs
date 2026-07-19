using System.Collections.Generic;
using DiffPlex;
using DiffPlex.Chunkers;

namespace Mainguard.Git.Services;

/// <summary>
/// Pure intra-line (word-level) diff engine (T-13). Given the old and new text of a changed
/// line pair, it returns the character sub-ranges that actually differ on each side, so the
/// diff viewer can emphasize only the changed words instead of the whole line. No repo, no IO,
/// no Avalonia — strings in, span ranges out — which is what makes it unit-testable.
///
/// Offsets are UTF-16 character indices into the supplied string. Span boundaries are snapped
/// outward so they never fall between the two halves of a surrogate pair (emoji / astral code
/// points): an emphasized run always covers whole code points.
/// </summary>
public static class IntraLineDiff
{
    // Reused, stateless chunker — DiffPlex's WordChunker holds no per-call state.
    private static readonly WordChunker Chunker = new();

    // Above this changed fraction (on BOTH sides) the word-level emphasis stops carrying signal:
    // the line-level add/remove tint already says "this line changed", and an emphasis run covering
    // essentially the whole line just re-shouts it louder. 0.95 keeps emphasis for lines that share
    // even a small stable prefix/suffix (e.g. indentation + a trailing brace).
    private const double EmphasisNoiseThreshold = 0.95;

    /// <summary>
    /// Computes the changed character spans on the old and new side of a modified line pair.
    /// Identical lines yield two empty lists. A wholly-rewritten line yields a single span
    /// covering the whole line on each side.
    /// </summary>
    public static (IReadOnlyList<(int Start, int Length)> Old, IReadOnlyList<(int Start, int Length)> New)
        Compute(string? oldLine, string? newLine)
    {
        oldLine ??= string.Empty;
        newLine ??= string.Empty;

        if (oldLine.Length == 0 && newLine.Length == 0)
            return (System.Array.Empty<(int, int)>(), System.Array.Empty<(int, int)>());

        // Word-level diff (whitespace significant here: whitespace-only edits still highlight).
        var result = Differ.Instance.CreateDiffs(oldLine, newLine, ignoreWhiteSpace: false, ignoreCase: false, Chunker);

        var oldOffsets = PrefixOffsets(result.PiecesOld, oldLine.Length);
        var newOffsets = PrefixOffsets(result.PiecesNew, newLine.Length);

        var oldSpans = new List<(int, int)>();
        var newSpans = new List<(int, int)>();

        foreach (var block in result.DiffBlocks)
        {
            if (block.DeleteCountA > 0)
                AddSpan(oldSpans, oldLine, oldOffsets[block.DeleteStartA], oldOffsets[block.DeleteStartA + block.DeleteCountA]);
            if (block.InsertCountB > 0)
                AddSpan(newSpans, newLine, newOffsets[block.InsertStartB], newOffsets[block.InsertStartB + block.InsertCountB]);
        }

        return (oldSpans, newSpans);
    }

    /// <summary>
    /// <see cref="Compute"/> filtered through the display-emphasis policy (T-13 quality): when the
    /// paired lines share (almost) nothing — the changed spans cover ≥ 95% of <em>both</em> sides —
    /// the word-level emphasis is suppressed (both lists empty). A wholly-rewritten pair reads as
    /// "this line was replaced", which the line-level tint already communicates; painting the whole
    /// line in the stronger emphasis colour on top of it is noise, not signal. Positional pairing
    /// (k-th delete ↔ k-th add) routinely pairs unrelated lines, so this case is common in practice.
    /// Callers that need the raw spans (tests, future heuristics) keep using <see cref="Compute"/>.
    /// </summary>
    public static (IReadOnlyList<(int Start, int Length)> Old, IReadOnlyList<(int Start, int Length)> New)
        ComputeEmphasis(string? oldLine, string? newLine)
    {
        var (oldSpans, newSpans) = Compute(oldLine, newLine);
        if (oldSpans.Count == 0 && newSpans.Count == 0) return (oldSpans, newSpans);

        // A zero-length side counts as fully changed (a content line paired against an empty one
        // shares nothing with it).
        bool oldNoisy = IsNoisy(oldSpans, (oldLine ?? string.Empty).Length);
        bool newNoisy = IsNoisy(newSpans, (newLine ?? string.Empty).Length);
        if (oldNoisy && newNoisy)
            return (System.Array.Empty<(int, int)>(), System.Array.Empty<(int, int)>());

        return (oldSpans, newSpans);
    }

    private static bool IsNoisy(IReadOnlyList<(int Start, int Length)> spans, int lineLength)
    {
        if (lineLength == 0) return true;
        int covered = 0;
        foreach (var (_, length) in spans) covered += length;
        return covered >= EmphasisNoiseThreshold * lineLength;
    }

    // Cumulative character offset of each piece boundary: offsets[i] is the start index of piece i,
    // offsets[^1] is the total length. If DiffPlex's pieces don't reconstruct the string exactly
    // (they always should), the final entry is pinned to the true length so callers never overrun.
    private static int[] PrefixOffsets(IReadOnlyList<string> pieces, int totalLength)
    {
        var offsets = new int[pieces.Count + 1];
        int acc = 0;
        for (int i = 0; i < pieces.Count; i++)
        {
            offsets[i] = acc;
            acc += pieces[i].Length;
        }
        offsets[pieces.Count] = totalLength;
        return offsets;
    }

    // Clamps to [0, line.Length], snaps both ends outward off any surrogate-pair midpoint, and
    // merges into an already-sorted span list (DiffPlex emits blocks in ascending order per side).
    private static void AddSpan(List<(int, int)> spans, string line, int start, int end)
    {
        start = System.Math.Clamp(start, 0, line.Length);
        end = System.Math.Clamp(end, 0, line.Length);
        if (end <= start) return;

        start = SnapStart(line, start);
        end = SnapEnd(line, end);
        if (end <= start) return;

        // Coalesce with the previous span if they now touch/overlap (can happen after snapping).
        if (spans.Count > 0)
        {
            var (pStart, pLen) = spans[^1];
            if (start <= pStart + pLen)
            {
                int newEnd = System.Math.Max(pStart + pLen, end);
                spans[^1] = (pStart, newEnd - pStart);
                return;
            }
        }
        spans.Add((start, end - start));
    }

    // A boundary that lands between a high surrogate and its trailing low surrogate would split
    // an astral code point. Snap both ends outward (start back, end forward) so the whole code
    // point stays inside the emphasized run rather than being cut in half.
    private static bool SplitsPair(string line, int index)
        => index > 0 && index < line.Length
           && char.IsLowSurrogate(line[index]) && char.IsHighSurrogate(line[index - 1]);

    private static int SnapStart(string line, int index) => SplitsPair(line, index) ? index - 1 : index;

    private static int SnapEnd(string line, int index) => SplitsPair(line, index) ? index + 1 : index;
}
