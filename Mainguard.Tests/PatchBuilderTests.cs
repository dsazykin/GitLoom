using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

// TI-06: PatchBuilder is pure. Hunk subsets are verbatim; line subsets implement the
// git add -p split (drop unselected adds, turn unselected deletes into context) and recount.
public class PatchBuilderTests
{
    private const string Header =
        "diff --git a/x.txt b/x.txt\nindex 1111111..2222222 100644\n--- a/x.txt\n+++ b/x.txt\n";

    private static DiffLine Ctx(string t) => new() { Kind = DiffLineKind.Context, Text = t };
    private static DiffLine Add(string t) => new() { Kind = DiffLineKind.Add, Text = t };
    private static DiffLine Del(string t) => new() { Kind = DiffLineKind.Delete, Text = t };

    private static FilePatch OneHunk(int oldStart, int newStart, params DiffLine[] lines) =>
        new() { Header = Header, Hunks = new[] { new DiffHunk { OldStart = oldStart, NewStart = newStart, Lines = lines } } };

    [Fact]
    public void BuildLinePatch_SelectingSingleAdd_ShouldContextualizeUnselectedDeletes()
    {
        // Worked example from the plan: context a, delete b, add B, delete c, context d.
        var file = OneHunk(10, 10, Ctx("a"), Del("b"), Add("B"), Del("c"), Ctx("d"));

        var patch = PatchBuilder.BuildLinePatch(file, 0, new[] { 2 }); // only "+B"

        Assert.Equal(Header + "@@ -10,4 +10,5 @@\n a\n b\n+B\n c\n d\n", patch);
    }

    [Fact]
    public void BuildLinePatch_OnlyAdditionsSelected_ShouldRecountCorrectly()
    {
        var file = OneHunk(5, 5, Ctx("x"), Add("y"), Add("z"));

        var patch = PatchBuilder.BuildLinePatch(file, 0, new[] { 1 }); // only "+y", drop "+z"

        Assert.Equal(Header + "@@ -5,1 +5,2 @@\n x\n+y\n", patch);
    }

    [Fact]
    public void BuildLinePatch_OnlyDeletionsSelected_ShouldTurnUnselectedDeletesToContext_DropUnselectedAdds()
    {
        var file = OneHunk(3, 3, Del("a"), Add("A"), Del("b"));

        var patch = PatchBuilder.BuildLinePatch(file, 0, new[] { 0 }); // only "-a"

        // "+A" dropped, "-b" becomes context.
        Assert.Equal(Header + "@@ -3,2 +3,1 @@\n-a\n b\n", patch);
    }

    [Fact]
    public void BuildLinePatch_NothingSelected_ShouldReturnEmpty()
    {
        var file = OneHunk(1, 1, Ctx("a"), Del("b"), Add("B"));

        Assert.Equal("", PatchBuilder.BuildLinePatch(file, 0, Array.Empty<int>()));
        // Selecting only a context line is not a change either.
        Assert.Equal("", PatchBuilder.BuildLinePatch(file, 0, new[] { 0 }));
    }

    [Fact]
    public void BuildHunkPatch_ShouldEmitHeaderPlusSelectedHunksVerbatim()
    {
        var original = ReadCorpus("06-adjacent-hunks.patch");
        var file = PatchParser.Parse(original).Single();

        // Selecting only the second hunk emits the header + that hunk, verbatim.
        var second = PatchBuilder.BuildHunkPatch(file, new[] { 1 });
        Assert.Equal(file.Header + "@@ -4 +4 @@ c3\n-c4\n+X4\n", second);
        Assert.DoesNotContain("-c2", second);

        // Selecting both hunks reproduces the whole patch.
        Assert.Equal(original, PatchBuilder.BuildHunkPatch(file, new[] { 0, 1 }));
    }

    [Fact]
    public void BuildHunkPatch_EmptySelection_ShouldReturnEmpty()
    {
        var file = PatchParser.Parse(ReadCorpus("06-adjacent-hunks.patch")).Single();
        Assert.Equal("", PatchBuilder.BuildHunkPatch(file, Array.Empty<int>()));
    }

    private static string ReadCorpus(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return File.ReadAllText(Path.Combine(dir ?? AppContext.BaseDirectory, "Mainguard.Tests", "TestData", "patches", fileName));
    }
}
