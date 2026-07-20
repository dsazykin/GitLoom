namespace Mainguard.Git.Services;

using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.Model;
using Mainguard.Git.Models;

/// <summary>
/// Pure 3-way merge chunker: strings in, ordered chunks out. No repository access, no file I/O,
/// no mutable static state — everything here is a function of its inputs (see T-02 plan §1, §7).
/// <paramref name="leftText"/> is "ours", <paramref name="rightText"/> is "theirs".
/// </summary>
public sealed class MergeDiffService : IMergeDiffService
{
    // Maps a base range [BaseStart, BaseEnd) to the side range it became, so replacement blocks
    // (delete + insert in one block) are handled uniformly with pure inserts/deletes.
    private readonly record struct Block(int BaseStart, int BaseEnd, int InsCount, int DelCount, int InsStart);

    public IReadOnlyList<MergeChunk> GenerateMergeChunks(string? baseText, string? leftText, string? rightText)
    {
        string[] baseLines = SplitLines(baseText, out _);
        string[] leftLines = SplitLines(leftText, out _);
        string[] rightLines = SplitLines(rightText, out _);

        List<Block> leftBlocks = ToBlocks(baseLines, leftLines);
        List<Block> rightBlocks = ToBlocks(baseLines, rightLines);

        int n = baseLines.Length;

        // Phase 3 — mark hotness over base coordinates.
        var leftChanged = new bool[n];
        var rightChanged = new bool[n];
        var leftInsAnchor = new bool[n + 1];
        var rightInsAnchor = new bool[n + 1];
        MarkHotness(leftBlocks, leftChanged, leftInsAnchor);
        MarkHotness(rightBlocks, rightChanged, rightInsAnchor);

        var chunks = new List<MergeChunk>();
        var pendingBase = new List<string>();

        int i = 0;
        while (i <= n)
        {
            bool anchorHot = leftInsAnchor[i] || rightInsAnchor[i];
            bool lineHot = i < n && (leftChanged[i] || rightChanged[i]);

            if (!anchorHot && !lineHot)
            {
                if (i < n) { pendingBase.Add(baseLines[i]); i++; }
                else i++;
                continue;
            }

            // ---- a region starts at s = i ----
            FlushPending(chunks, pendingBase);
            int s = i;
            int e = i;
            while (e < n && (leftChanged[e] || rightChanged[e])) e++;

            EmitRegion(chunks, baseLines, leftLines, rightLines, leftBlocks, rightBlocks, s, e);

            if (e > s)
            {
                for (int a = s; a <= e; a++) { leftInsAnchor[a] = false; rightInsAnchor[a] = false; }
                i = e;
            }
            else
            {
                // Zero-length region: base line s is still unchanged; consume the anchor without advancing.
                leftInsAnchor[s] = false;
                rightInsAnchor[s] = false;
            }
        }

        FlushPending(chunks, pendingBase);
        return chunks;
    }

    public string AssembleMerged(IEnumerable<MergeChunk> chunks)
    {
        var lines = new List<string>();
        foreach (var c in chunks)
        {
            string chosen = c.Kind switch
            {
                ChunkKind.Unchanged => c.BaseText,
                ChunkKind.LeftOnly => c.LeftText,
                ChunkKind.RightOnly => c.RightText,
                ChunkKind.Conflict => c.Resolution switch
                {
                    ChunkResolution.TakeLeft => c.LeftText,
                    ChunkResolution.TakeRight => c.RightText,
                    ChunkResolution.TakeBoth => Combine(c.LeftText, c.RightText),
                    ChunkResolution.Custom => c.CustomText ?? "",
                    _ /* Unresolved */        => throw new InvalidOperationException(
                                                     "Cannot assemble: unresolved conflict chunk."),
                },
                _ => "",
            };
            if (chosen.Length == 0)
            {
                // The chunk texts are joined slices, so "" is ambiguous: an EMPTY slice (e.g.
                // take-ours of a deletion — contributes nothing) or a slice of exactly ONE EMPTY
                // LINE. For an Unchanged chunk only the latter exists: GenerateMergeChunks never
                // emits an empty Unchanged chunk (FlushPending skips an empty pending run, and a
                // hot region is by construction non-empty on some side), so BaseText=="" here IS
                // a blank base line — dropping it would eat the blank line between two edited
                // regions on every resolve ("never lose work"). For changed/resolved sides ""
                // still means an empty slice; a resolved side of exactly one blank line remains
                // unrepresentable in the joined-string chunk model (KNOWN LIMIT, tests pin both).
                if (c.Kind == ChunkKind.Unchanged) lines.Add(string.Empty);
                continue;
            }
            lines.AddRange(chosen.Split('\n'));
        }
        if (lines.Count == 0) return "";
        // POLICY (pinned by tests): a non-empty merged document ends with exactly one trailing newline.
        // AssembleMerged only receives chunks, not the original trailing-newline flags, so we cannot
        // faithfully reproduce a missing final newline; adding one is the accepted v1 behavior.
        return string.Join("\n", lines) + "\n";
    }

    private static string Combine(string left, string right)   // TakeBoth: left block then right block
        => left.Length == 0 ? right : right.Length == 0 ? left : left + "\n" + right;

    // ---- Phase 1 — normalize + split. "" -> zero lines (NOT one empty line). ----
    private static string[] SplitLines(string? text, out bool hadTrailingNewline)
    {
        text ??= "";
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        if (text.Length == 0) { hadTrailingNewline = false; return Array.Empty<string>(); }
        hadTrailingNewline = text[^1] == '\n';
        var parts = text.Split('\n');
        return hadTrailingNewline ? parts[..^1] : parts;
    }

    // ---- Phase 2 — diff base against a side, extract blocks in ascending base order. ----
    private static List<Block> ToBlocks(string[] baseLines, string[] sideLines)
    {
        string baseJoined = string.Join("\n", baseLines);
        string sideJoined = string.Join("\n", sideLines);
        DiffResult r = Differ.Instance.CreateDiffs(baseJoined, sideJoined, false, false, new LineChunker());
        var blocks = new List<Block>(r.DiffBlocks.Count);
        foreach (var b in r.DiffBlocks)
        {
            blocks.Add(new Block(
                BaseStart: b.DeleteStartA,
                BaseEnd: b.DeleteStartA + b.DeleteCountA,
                InsCount: b.InsertCountB,
                DelCount: b.DeleteCountA,
                InsStart: b.InsertStartB));
        }
        return blocks;
    }

    // ---- Phase 3 — hotness bookkeeping. ----
    private static void MarkHotness(List<Block> blocks, bool[] changed, bool[] insAnchor)
    {
        foreach (var b in blocks)
        {
            if (b.DelCount > 0)
                for (int idx = b.BaseStart; idx < b.BaseEnd; idx++) changed[idx] = true;
            else
                insAnchor[b.BaseStart] = true;
        }
    }

    // ---- Phase 4 — offset-mapping reconstruction. ----
    // Maps a base index to the corresponding side index by accumulating each fully-passed block's
    // (InsCount - DelCount). inclusiveAnchor decides whether a zero-length insertion block sitting
    // exactly at baseIdx counts as "before" the index (true for a region END, false for its START).
    private static int SideIndex(List<Block> blocks, int baseIdx, bool inclusiveAnchor)
    {
        int idx = baseIdx;
        foreach (var b in blocks)   // ascending base order
        {
            if (b.BaseStart >= baseIdx && !(inclusiveAnchor && b.BaseStart == baseIdx && b.BaseEnd == baseIdx))
                break;
            bool countIt = inclusiveAnchor
                ? b.BaseEnd <= baseIdx
                : b.BaseEnd <= baseIdx && b.BaseStart < baseIdx;
            if (countIt) idx += b.InsCount - b.DelCount;
        }
        return idx;
    }

    private static string[] Slice(List<Block> blocks, string[] sideLines, int s, int e)
    {
        int start = SideIndex(blocks, s, inclusiveAnchor: false);
        int end = SideIndex(blocks, e, inclusiveAnchor: true);
        return sideLines[start..end];
    }

    private static void FlushPending(List<MergeChunk> chunks, List<string> pendingBase)
    {
        if (pendingBase.Count == 0) return;
        string joined = string.Join("\n", pendingBase);
        chunks.Add(new MergeChunk { Kind = ChunkKind.Unchanged, BaseText = joined, LeftText = joined, RightText = joined });
        pendingBase.Clear();
    }

    private static void EmitRegion(
        List<MergeChunk> chunks,
        string[] baseLines, string[] leftLines, string[] rightLines,
        List<Block> leftBlocks, List<Block> rightBlocks,
        int s, int e)
    {
        string[] baseSlice = baseLines[s..e];
        string[] leftSlice = Slice(leftBlocks, leftLines, s, e);
        string[] rightSlice = Slice(rightBlocks, rightLines, s, e);

        bool lDiff = !leftSlice.AsSpan().SequenceEqual(baseSlice);
        bool rDiff = !rightSlice.AsSpan().SequenceEqual(baseSlice);

        ChunkKind kind;
        if (!lDiff && !rDiff) kind = ChunkKind.Unchanged;               // defensive
        else if (lDiff && !rDiff) kind = ChunkKind.LeftOnly;
        else if (!lDiff && rDiff) kind = ChunkKind.RightOnly;
        else if (leftSlice.AsSpan().SequenceEqual(rightSlice)) kind = ChunkKind.LeftOnly;  // identical edits merge cleanly
        else kind = ChunkKind.Conflict;

        chunks.Add(new MergeChunk
        {
            Kind = kind,
            BaseText = string.Join("\n", baseSlice),
            LeftText = string.Join("\n", leftSlice),
            RightText = string.Join("\n", rightSlice),
        });
    }
}
