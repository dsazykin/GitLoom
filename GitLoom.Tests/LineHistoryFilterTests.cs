using GitLoom.Core.Models;
using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Pure unit tests for the T-12 line-history filter geometry (reuses <see cref="PatchParser"/>).
/// Covers old-side vs new-side intersection, boundary overlap, omitted counts, reversed ranges, and
/// multi-hunk / empty patches.
/// </summary>
public class LineHistoryFilterTests
{
    [Theory]
    // hunk covering new lines 10..12 vs assorted query ranges
    [InlineData(10, 12, true)]    // exact
    [InlineData(1, 9, false)]     // entirely before
    [InlineData(13, 20, false)]   // entirely after
    [InlineData(9, 10, true)]     // overlaps low boundary
    [InlineData(12, 15, true)]    // overlaps high boundary
    [InlineData(5, 30, true)]     // superset
    [InlineData(11, 11, true)]    // single line inside
    public void PatchIntersectsRange_NewSideSpan(int start, int end, bool expected)
    {
        var patch = "@@ -10,3 +10,3 @@\n-a\n-b\n-c\n+A\n+B\n+C\n";
        Assert.Equal(expected, LineHistoryFilter.PatchIntersectsRange(patch, start, end));
    }

    [Fact]
    public void PatchIntersectsRange_ShouldMatchOldSide_ForPureDeletion()
    {
        // A pure deletion at old lines 5..6 (new side collapses to line 4 with count 0). Querying the
        // old-side range must still register the touch.
        var patch = "@@ -5,2 +4,0 @@\n-gone1\n-gone2\n";
        Assert.True(LineHistoryFilter.PatchIntersectsRange(patch, 5, 6));
        Assert.False(LineHistoryFilter.PatchIntersectsRange(patch, 20, 25));
    }

    [Fact]
    public void PatchIntersectsRange_ShouldHandleOmittedCounts()
    {
        // git omits ",1": `@@ -7 +7 @@` is a single-line change at line 7.
        var patch = "@@ -7 +7 @@\n-old\n+new\n";
        Assert.True(LineHistoryFilter.PatchIntersectsRange(patch, 7, 7));
        Assert.True(LineHistoryFilter.PatchIntersectsRange(patch, 5, 8));
        Assert.False(LineHistoryFilter.PatchIntersectsRange(patch, 8, 9));
    }

    [Fact]
    public void PatchIntersectsRange_ReversedRange_ShouldBeNormalized()
    {
        var patch = "@@ -10,3 +10,3 @@\n-a\n-b\n-c\n+A\n+B\n+C\n";
        Assert.True(LineHistoryFilter.PatchIntersectsRange(patch, 12, 10));   // start > end still works
    }

    [Fact]
    public void PatchIntersectsRange_MultiHunk_ShouldMatchAnyHunk()
    {
        var patch =
            "@@ -1,1 +1,1 @@\n-a\n+A\n" +
            "@@ -50,1 +50,1 @@\n-b\n+B\n";
        Assert.True(LineHistoryFilter.PatchIntersectsRange(patch, 49, 51));   // second hunk
        Assert.False(LineHistoryFilter.PatchIntersectsRange(patch, 20, 30));  // between hunks
    }

    [Fact]
    public void PatchIntersectsRange_EmptyOrNull_ShouldBeFalse()
    {
        Assert.False(LineHistoryFilter.PatchIntersectsRange("", 1, 5));
        Assert.False(LineHistoryFilter.PatchIntersectsRange("diff --git a/x b/x\n", 1, 5));
    }

    [Fact]
    public void HunkIntersects_Direct_ShouldRespectBothSides()
    {
        var hunk = new DiffHunk { OldStart = 100, OldCount = 2, NewStart = 3, NewCount = 2 };
        Assert.True(LineHistoryFilter.HunkIntersects(hunk, 3, 4));      // new-side hit
        Assert.True(LineHistoryFilter.HunkIntersects(hunk, 100, 101)); // old-side hit
        Assert.False(LineHistoryFilter.HunkIntersects(hunk, 50, 60));  // neither
    }
}
