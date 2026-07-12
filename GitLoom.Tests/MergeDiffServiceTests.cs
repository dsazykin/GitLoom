using GitLoom.Core.Models;
using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

public class MergeDiffServiceTests
{
    private static IMergeDiffService NewService() => new MergeDiffService();

    [Fact]
    public void GenerateMergeChunks_Identical_ShouldYieldSingleUnchanged()
    {
        var chunks = NewService().GenerateMergeChunks("a\nb\nc", "a\nb\nc", "a\nb\nc");
        var chunk = Assert.Single(chunks);
        Assert.Equal(ChunkKind.Unchanged, chunk.Kind);
        Assert.Equal("a\nb\nc", chunk.BaseText);
    }

    [Fact]
    public void GenerateMergeChunks_AllEmpty_ShouldYieldEmptyList()
    {
        var chunks = NewService().GenerateMergeChunks("", "", "");
        Assert.Empty(chunks);
    }

    [Fact]
    public void GenerateMergeChunks_LeftOnlyEdit_ShouldYieldLeftOnlyChunk()
    {
        var chunks = NewService().GenerateMergeChunks("a\nb\nc", "a\nX\nc", "a\nb\nc");
        Assert.Collection(chunks,
            c => { Assert.Equal(ChunkKind.Unchanged, c.Kind); Assert.Equal("a", c.BaseText); },
            c => { Assert.Equal(ChunkKind.LeftOnly, c.Kind); Assert.Equal("b", c.BaseText); Assert.Equal("X", c.LeftText); },
            c => { Assert.Equal(ChunkKind.Unchanged, c.Kind); Assert.Equal("c", c.BaseText); });
    }

    [Fact]
    public void GenerateMergeChunks_RightOnlyEdit_ShouldYieldRightOnlyChunk()
    {
        var chunks = NewService().GenerateMergeChunks("a\nb\nc", "a\nb\nc", "a\nX\nc");
        Assert.Collection(chunks,
            c => { Assert.Equal(ChunkKind.Unchanged, c.Kind); Assert.Equal("a", c.BaseText); },
            c => { Assert.Equal(ChunkKind.RightOnly, c.Kind); Assert.Equal("b", c.BaseText); Assert.Equal("X", c.RightText); },
            c => { Assert.Equal(ChunkKind.Unchanged, c.Kind); Assert.Equal("c", c.BaseText); });
    }

    [Fact]
    public void GenerateMergeChunks_SameLineEditedBothSides_ShouldYieldConflict()
    {
        var chunks = NewService().GenerateMergeChunks("a\nb\nc", "a\nX\nc", "a\nY\nc");
        var conflict = Assert.Single(chunks, c => c.Kind == ChunkKind.Conflict);
        Assert.Equal("X", conflict.LeftText);
        Assert.Equal("Y", conflict.RightText);
    }

    [Fact]
    public void GenerateMergeChunks_IdenticalEditBothSides_ShouldNotConflict()
    {
        var chunks = NewService().GenerateMergeChunks("a\nb\nc", "a\nX\nc", "a\nX\nc");
        Assert.DoesNotContain(chunks, c => c.Kind == ChunkKind.Conflict);
        var mid = Assert.Single(chunks, c => c.Kind == ChunkKind.LeftOnly);
        Assert.Equal("X", mid.LeftText);
    }

    [Fact]
    public void GenerateMergeChunks_NonOverlappingEdits_ShouldYieldBothKinds_AndAssembleToTrueMerge()
    {
        var svc = NewService();
        var chunks = svc.GenerateMergeChunks("1\n2\n3\n4\n5", "1\nL\n3\n4\n5", "1\n2\n3\nR\n5");
        Assert.Collection(chunks,
            c => Assert.Equal(ChunkKind.Unchanged, c.Kind),
            c => Assert.Equal(ChunkKind.LeftOnly, c.Kind),
            c => Assert.Equal(ChunkKind.Unchanged, c.Kind),
            c => Assert.Equal(ChunkKind.RightOnly, c.Kind),
            c => Assert.Equal(ChunkKind.Unchanged, c.Kind));
        Assert.Equal("1\nL\n3\nR\n5\n", svc.AssembleMerged(chunks));
    }

    [Theory]
    [InlineData("a\nb\nc", "a\nL\nb\nc", "a\nR\nb\nc")]   // both insert mid-file at the same gap
    [InlineData("a\nb\nc", "a\nb\nc\nL", "a\nb\nc\nR")]   // both append at EOF
    public void GenerateMergeChunks_BothInsertAtSameAnchor_ShouldConflict(string b, string l, string r)
    {
        var chunks = NewService().GenerateMergeChunks(b, l, r);
        Assert.Contains(chunks, c => c.Kind == ChunkKind.Conflict);
    }

    [Fact]
    public void GenerateMergeChunks_AddAdd_EmptyBase_ShouldConflict()
    {
        var chunks = NewService().GenerateMergeChunks("", "L", "R");
        var conflict = Assert.Single(chunks, c => c.Kind == ChunkKind.Conflict);
        Assert.Equal("", conflict.BaseText);
    }

    [Fact]
    public void GenerateMergeChunks_WholeFileDeleteVsEdit_ShouldConflict_WithEmptyLeftText()
    {
        var chunks = NewService().GenerateMergeChunks("a\nb\nc", "", "a\nZ\nc");
        var conflict = Assert.Single(chunks, c => c.Kind == ChunkKind.Conflict);
        Assert.Equal("", conflict.LeftText);
    }

    [Fact]
    public void GenerateMergeChunks_CrlfInput_ShouldBehaveAsLf()
    {
        var chunks = NewService().GenerateMergeChunks("a\r\nb\r\nc", "a\r\nX\r\nc", "a\r\nY\r\nc");
        var conflict = Assert.Single(chunks, c => c.Kind == ChunkKind.Conflict);
        Assert.Equal("X", conflict.LeftText);
        Assert.Equal("Y", conflict.RightText);
        Assert.All(chunks, c =>
        {
            Assert.DoesNotContain('\r', c.BaseText);
            Assert.DoesNotContain('\r', c.LeftText);
            Assert.DoesNotContain('\r', c.RightText);
        });
    }

    [Fact]
    public void AssembleMerged_Unresolved_ShouldThrowInvalidOperation()
    {
        var chunk = new MergeChunk { Kind = ChunkKind.Conflict, LeftText = "X", RightText = "Y" };
        Assert.Throws<InvalidOperationException>(() => NewService().AssembleMerged(new[] { chunk }));
    }

    [Theory]
    [InlineData(ChunkResolution.TakeLeft, null, "X\n")]
    [InlineData(ChunkResolution.TakeRight, null, "Y\n")]
    [InlineData(ChunkResolution.TakeBoth, null, "X\nY\n")]
    [InlineData(ChunkResolution.Custom, "Z", "Z\n")]
    public void AssembleMerged_Resolutions_ShouldEmitChosenText(ChunkResolution res, string? custom, string expected)
    {
        var chunk = new MergeChunk
        {
            Kind = ChunkKind.Conflict,
            LeftText = "X",
            RightText = "Y",
            Resolution = res,
            CustomText = custom,
        };
        Assert.Equal(expected, NewService().AssembleMerged(new[] { chunk }));
    }

    public static IEnumerable<object[]> CoverageTriples() => new List<object[]>
    {
        new object[] { "a\nb\nc", "a\nb\nc", "a\nb\nc" },
        new object[] { "a\nb\nc", "a\nX\nc", "a\nb\nc" },
        new object[] { "a\nb\nc", "a\nb\nc", "a\nY\nc" },
        new object[] { "a\nb\nc", "a\nX\nc", "a\nY\nc" },
        new object[] { "1\n2\n3\n4\n5", "1\nL\n3\n4\n5", "1\n2\n3\nR\n5" },
        new object[] { "a\nb\nc", "a\nL\nb\nc", "a\nR\nb\nc" },
        new object[] { "a\nb\nc", "", "a\nZ\nc" },
        new object[] { "", "L", "R" },
        new object[] { "a\nb\nc\nd", "a\nb\nc\nd\ne", "A\nb\nc\nd" },
        new object[] { "x\ny\nz", "x\nY1\nz", "x\nY1\nz" },
    };

    [Theory]
    [MemberData(nameof(CoverageTriples))]
    public void Chunks_ShouldCoverBaseExactly(string baseText, string left, string right)
    {
        var chunks = NewService().GenerateMergeChunks(baseText, left, right);

        // (a) concatenating chunk BaseTexts with "\n", skipping empty, reproduces base
        string normBase = baseText.Replace("\r\n", "\n").Replace("\r", "\n");
        if (normBase.EndsWith('\n')) normBase = normBase[..^1];
        string reconstructed = string.Join("\n", chunks.Select(c => c.BaseText).Where(t => t.Length > 0));
        Assert.Equal(normBase, reconstructed);

        // (b) no two adjacent chunks share a Kind (unless a Conflict separates them)
        for (int i = 1; i < chunks.Count; i++)
        {
            if (chunks[i].Kind == chunks[i - 1].Kind && chunks[i].Kind != ChunkKind.Conflict)
                Assert.Fail($"Adjacent chunks share Kind {chunks[i].Kind} at index {i}");
        }

        // (c) no Conflict chunk has LeftText == RightText
        Assert.DoesNotContain(chunks, c => c.Kind == ChunkKind.Conflict && c.LeftText == c.RightText);
    }

    // ---- Blank-line conservation through AssembleMerged (Lane H Part 5, "never lose work") ------

    [Fact]
    public void AssembleMerged_BlankBaseLineBetweenTwoEditedRegions_IsPreserved()
    {
        // base:  x / (blank) / y   left edits x→X, right edits y→Y. The blank line sits in an
        // Unchanged chunk whose joined BaseText is "" — before the fix it was dropped from the
        // assembly, silently eating the user's blank line on every resolve.
        var svc = NewService();
        var chunks = svc.GenerateMergeChunks("x\n\ny\n", "X\n\ny\n", "x\n\nY\n");

        var merged = svc.AssembleMerged(chunks);

        Assert.Equal("X\n\nY\n", merged);
    }

    [Fact]
    public void AssembleMerged_DocumentOfOneBlankLine_RoundTrips()
    {
        var svc = NewService();
        var chunks = svc.GenerateMergeChunks("\n", "\n", "\n");

        Assert.Equal("\n", svc.AssembleMerged(chunks));
    }

    [Fact]
    public void AssembleMerged_TakeOursOfADeletion_StillContributesNothing()
    {
        // The other face of the "" ambiguity: a LeftOnly deletion (left slice genuinely empty)
        // must keep contributing zero lines — the fix applies to Unchanged chunks only.
        var svc = NewService();
        var chunks = svc.GenerateMergeChunks("a\nb\nc\n", "a\nc\n", "a\nb\nc\n");

        Assert.Equal("a\nc\n", svc.AssembleMerged(chunks));
    }

    [Fact]
    public void AssembleMerged_ResolvedSideOfExactlyOneBlankLine_KnownLimit_CollapsesToNothing()
    {
        // KNOWN LIMIT (pinned so a future fix is deliberate): the joined-string chunk model cannot
        // distinguish a resolved side that is one blank line from an empty side — both are "".
        // Replacing a line with a single blank line therefore assembles as a deletion. Fixing this
        // requires carrying line counts on MergeChunk (a model change), not an assembler tweak.
        var svc = NewService();
        var chunks = svc.GenerateMergeChunks("a\nZZZ\nc\n", "a\n\nc\n", "a\nZZZ\nc\n");

        Assert.Equal("a\nc\n", svc.AssembleMerged(chunks));
    }
}
