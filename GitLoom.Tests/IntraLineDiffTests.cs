using System.Collections.Generic;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-13 (pure): intra-line word-diff engine. Segment ranges are pinned exactly — these are the
// contract the diff viewer's emphasis renders against. No repo, no Avalonia.
public class IntraLineDiffTests
{
    [Fact]
    public void HighlightSpans_SingleWordChange_ShouldSpanOnlyThatWord()
    {
        var (old, @new) = IntraLineDiff.Compute("the cat sat", "the dog sat");

        Assert.Equal(new (int, int)[] { (4, 3) }, old);
        Assert.Equal(new (int, int)[] { (4, 3) }, @new);
        Assert.Equal("cat", "the cat sat".Substring(old[0].Start, old[0].Length));
        Assert.Equal("dog", "the dog sat".Substring(@new[0].Start, @new[0].Length));
    }

    [Fact]
    public void HighlightSpans_FullRewrite_ShouldSpanWholeLine()
    {
        var (old, @new) = IntraLineDiff.Compute("abc", "xyz");

        Assert.Equal(new (int, int)[] { (0, 3) }, old);
        Assert.Equal(new (int, int)[] { (0, 3) }, @new);
    }

    [Fact]
    public void HighlightSpans_IdenticalLines_ShouldBeEmpty()
    {
        var (old, @new) = IntraLineDiff.Compute("no change here", "no change here");

        Assert.Empty(old);
        Assert.Empty(@new);
    }

    // ---- ComputeEmphasis: the display policy over Compute (T-13 quality) ------------------------

    [Fact]
    public void ComputeEmphasis_PartialChange_KeepsTheWordSpans()
    {
        var (old, @new) = IntraLineDiff.ComputeEmphasis("the cat sat", "the dog sat");

        Assert.Equal(new (int, int)[] { (4, 3) }, old);
        Assert.Equal(new (int, int)[] { (4, 3) }, @new);
    }

    [Fact]
    public void ComputeEmphasis_WhollyRewrittenPair_SuppressesTheNoise()
    {
        // Compute() reports whole-line spans on both sides; the display policy drops them — the
        // line-level tint already communicates "replaced", whole-line emphasis on top is noise.
        var (old, @new) = IntraLineDiff.ComputeEmphasis("abc", "xyz");

        Assert.Empty(old);
        Assert.Empty(@new);
    }

    [Fact]
    public void ComputeEmphasis_ContentPairedAgainstEmpty_SuppressesTheNoise()
    {
        var (old, @new) = IntraLineDiff.ComputeEmphasis("", "entirely new line");

        Assert.Empty(old);
        Assert.Empty(@new);
    }

    [Fact]
    public void ComputeEmphasis_SharedPrefixOrSuffix_KeepsEmphasis()
    {
        // The lines share the indentation + trailing brace — under the 95% threshold, so the
        // changed middle stays emphasized on both sides.
        var (old, @new) = IntraLineDiff.ComputeEmphasis(
            "    return oldValue; }", "    return newResult; }");

        Assert.NotEmpty(old);
        Assert.NotEmpty(@new);
    }

    [Fact]
    public void ComputeEmphasis_OneSideNoisyOtherNot_KeepsBothSidesSpans()
    {
        // Suppression requires BOTH sides to be noise — a short deleted word replaced by a long
        // rewrite still deserves its old-side emphasis, and the spans travel as a pair.
        var (old, @new) = IntraLineDiff.ComputeEmphasis(
            "keep this exact line", "keep nothing else of it at all here");

        Assert.Equal(IntraLineDiff.Compute("keep this exact line", "keep nothing else of it at all here").Old, old);
        Assert.NotEmpty(@new);
    }

    [Fact]
    public void HighlightSpans_EmptyOldLine_ShouldSpanWholeNewLine()
    {
        var (old, @new) = IntraLineDiff.Compute("", "abc");

        Assert.Empty(old);
        Assert.Equal(new (int, int)[] { (0, 3) }, @new);
    }

    [Fact]
    public void HighlightSpans_EmptyNewLine_ShouldSpanWholeOldLine()
    {
        var (old, @new) = IntraLineDiff.Compute("abc", "");

        Assert.Equal(new (int, int)[] { (0, 3) }, old);
        Assert.Empty(@new);
    }

    [Fact]
    public void HighlightSpans_WhitespaceOnlyChange_ShouldSpanOnlyTheWhitespaceRun()
    {
        // "a b" -> "a  b": only the middle whitespace run grows; the words are untouched.
        var (old, @new) = IntraLineDiff.Compute("a b", "a  b");

        Assert.Equal(new (int, int)[] { (1, 1) }, old);
        Assert.Equal(new (int, int)[] { (1, 2) }, @new);
        Assert.Equal(" ", "a b".Substring(old[0].Start, old[0].Length));
        Assert.Equal("  ", "a  b".Substring(@new[0].Start, @new[0].Length));
    }

    [Fact]
    public void HighlightSpans_CrlfVsLf_ShouldSurfaceTheCarriageReturnAsAChange()
    {
        // A CRLF-vs-LF line pair differs only by the trailing '\r' carried on the old text.
        var (old, @new) = IntraLineDiff.Compute("abc\r", "abc");

        Assert.Equal(new (int, int)[] { (0, 4) }, old);   // whole "abc\r" word flagged
        Assert.Equal(new (int, int)[] { (0, 3) }, @new);
        Assert.EndsWith("\r", "abc\r".Substring(old[0].Start, old[0].Length));
    }

    [Theory]
    [InlineData("a\U0001F600b", "a\U0001F600c")]          // trailing letter change, emoji kept
    [InlineData("\U0001F600", "\U0001F600\U0001F601")]    // an emoji appended
    [InlineData("\U0001F600 \U0001F601", "\U0001F600 \U0001F602")] // space-separated emoji word swap
    [InlineData("\U0001F468‍\U0001F469‍\U0001F467", "\U0001F468‍\U0001F469‍\U0001F466")] // ZWJ family
    public void HighlightSpans_ShouldNeverSplitSurrogatePairs(string oldLine, string newLine)
    {
        var (old, @new) = IntraLineDiff.Compute(oldLine, newLine);

        AssertNoSurrogateSplit(oldLine, old);
        AssertNoSurrogateSplit(newLine, @new);
    }

    [Fact]
    public void HighlightSpans_SpaceSeparatedEmojiChange_ShouldSpanTheChangedEmojiExactly()
    {
        var (_, @new) = IntraLineDiff.Compute("\U0001F600 \U0001F601", "\U0001F600 \U0001F602");

        var span = Assert.Single(@new);
        Assert.Equal("\U0001F602", "\U0001F600 \U0001F602".Substring(span.Start, span.Length));
    }

    private static void AssertNoSurrogateSplit(string line, IReadOnlyList<(int Start, int Length)> spans)
    {
        foreach (var (start, length) in spans)
        {
            // Neither boundary may sit between a high surrogate and its trailing low surrogate.
            Assert.False(SplitsPair(line, start), $"start {start} splits a surrogate pair in '{line}'");
            Assert.False(SplitsPair(line, start + length), $"end {start + length} splits a surrogate pair in '{line}'");
            // And the extracted run itself is well-formed (no lone surrogate at either end).
            var sub = line.Substring(start, length);
            Assert.False(char.IsLowSurrogate(sub[0]), "run starts on a low surrogate");
            Assert.False(char.IsHighSurrogate(sub[^1]), "run ends on a high surrogate");
        }
    }

    private static bool SplitsPair(string line, int index)
        => index > 0 && index < line.Length
           && char.IsLowSurrogate(line[index]) && char.IsHighSurrogate(line[index - 1]);
}
