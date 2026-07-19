using System;
using System.IO;
using System.Linq;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

// TI-13 (integration): the whitespace-aware GetFileDiff overload. Drives `git diff -w`, hence the trait.
[Trait("Category", "RequiresGitCli")]
public class GitServiceWhitespaceDiffTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    private static int HunkCount(string diff)
        => PatchParser.Parse(diff).Sum(p => p.Hunks.Count);

    [Fact]
    public void GetFileDiff_IgnoreWhitespace_ShouldYieldZeroHunks_ForIndentOnlyChange()
    {
        _fx.CommitFile("f.cs", "class C\n{\n    int x;\n}\n", "seed");
        // Re-indent the field with a tab — a whitespace-only change.
        _fx.WriteFile("f.cs", "class C\n{\n\t\tint x;\n}\n");

        // Sanity: a plain diff *does* show the change.
        Assert.True(HunkCount(_service.GetFileDiff(_fx.RepoPath, "f.cs", isStaged: false, ignoreWhitespace: false)) >= 1);

        // -w collapses it to nothing.
        var ws = _service.GetFileDiff(_fx.RepoPath, "f.cs", isStaged: false, ignoreWhitespace: true);
        Assert.Equal(0, HunkCount(ws));
    }

    [Fact]
    public void GetFileDiff_IgnoreWhitespace_ShouldKeepRealHunks_InMixedChange()
    {
        _fx.CommitFile("f.cs", "class C\n{\n    int x;\n    int y;\n}\n", "seed");
        // Re-indent one line (whitespace only) AND change another line's content.
        _fx.WriteFile("f.cs", "class C\n{\n\tint x;\n    int z;\n}\n");

        var ws = _service.GetFileDiff(_fx.RepoPath, "f.cs", isStaged: false, ignoreWhitespace: true);

        Assert.True(HunkCount(ws) >= 1);
        // The genuine content edit survives; the whitespace-only reindent does not.
        Assert.Contains("int z;", ws);
        Assert.DoesNotContain("-    int x;", ws);
    }

    [Fact]
    public void GetFileDiff_IgnoreWhitespace_Staged_ShouldYieldZeroHunks_ForIndentOnlyChange()
    {
        _fx.CommitFile("f.cs", "a\nb\nc\n", "seed");
        _fx.WriteFile("f.cs", "a\n  b\nc\n");
        // Stage the whitespace-only change; the staged (index vs HEAD) -w diff must also be empty.
        using (var repo = new LibGit2Sharp.Repository(_fx.RepoPath))
            LibGit2Sharp.Commands.Stage(repo, "f.cs");

        var ws = _service.GetFileDiff(_fx.RepoPath, "f.cs", isStaged: true, ignoreWhitespace: true);
        Assert.Equal(0, HunkCount(ws));
    }

    [Fact]
    public void GetFileDiff_IgnoreWhitespace_ShouldYieldZeroHunks_ForNoNewlineAtEofWhitespaceChange()
    {
        // Last line carries no trailing newline; the only change is trailing whitespace on it.
        _fx.CommitFile("f.txt", "line one\nlast", "seed");
        File.WriteAllText(Path.Combine(_fx.RepoPath, "f.txt"), "line one\nlast   ");

        var ws = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: false, ignoreWhitespace: true);
        Assert.Equal(0, HunkCount(ws));
    }
}
