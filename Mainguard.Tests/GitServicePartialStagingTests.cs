using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

// Backfill B-1 (test strategy doc): fix 1.13 shipped three partial-staging
// entry points but only StageHunk was covered. These drive git apply, hence
// the trait.
[Trait("Category", "RequiresGitCli")]
public class GitServicePartialStagingTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public GitServicePartialStagingTests()
    {
        // A 12-line file gives two well-separated hunks when lines 2 and 11 change.
        var original = string.Join("\n", Enumerable.Range(1, 12).Select(n => $"L{n}")) + "\n";
        _fx.CommitFile("f.txt", original, "seed");
    }

    public void Dispose() => _fx.Dispose();

    private void ModifyBothRegions()
    {
        var text = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt"))
            .Replace("L2\n", "L2-changed\n")
            .Replace("L11\n", "L11-changed\n");
        _fx.WriteFile("f.txt", text);
    }

    private static string FirstHunkOf(string patch)
    {
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        int i = 0;
        for (; i < lines.Length && !lines[i].StartsWith("@@"); i++)
            sb.Append(lines[i]).Append('\n');
        if (i < lines.Length) { sb.Append(lines[i]).Append('\n'); i++; }
        for (; i < lines.Length && !lines[i].StartsWith("@@"); i++)
            sb.Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void UnstageHunk_ShouldRemoveOnlySelectedHunk_FromIndex()
    {
        ModifyBothRegions();
        _service.StageFile(_fx.RepoPath, "f.txt"); // both hunks staged

        var stagedPatch = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true);
        _service.UnstageHunk(_fx.RepoPath, FirstHunkOf(stagedPatch));

        var staged = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true);
        var unstaged = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: false);

        Assert.DoesNotContain("L2-changed", staged);
        Assert.Contains("L11-changed", staged);
        Assert.Contains("L2-changed", unstaged);
        // The working tree keeps both edits regardless.
        var content = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt"));
        Assert.Contains("L2-changed", content);
        Assert.Contains("L11-changed", content);
    }

    [Fact]
    public void DiscardHunk_ShouldRevertOnlySelectedHunk_InWorkdir()
    {
        ModifyBothRegions();

        var patch = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: false);
        _service.DiscardHunk(_fx.RepoPath, FirstHunkOf(patch));

        var content = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt"));
        Assert.Contains("L2\n", content);              // first edit reverted
        Assert.DoesNotContain("L2-changed", content);
        Assert.Contains("L11-changed", content);       // second edit untouched
    }

    [Fact]
    public void StageHunk_ShouldThrowTyped_OnCorruptPatch()
    {
        ModifyBothRegions();
        Assert.Throws<GitOperationException>(
            () => _service.StageHunk(_fx.RepoPath, "this is not a unified diff\n"));
    }

    [Fact]
    public void StageHunk_ShouldNoOp_OnEmptyPatch()
    {
        ModifyBothRegions();
        _service.StageHunk(_fx.RepoPath, ""); // must not throw, must not launch git

        var staged = _service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true);
        Assert.DoesNotContain("L2-changed", staged);
        Assert.DoesNotContain("L11-changed", staged);
    }

    // ---- TI-06: PatchBuilder output must be accepted by git apply (ground truth) ----
    // These use a separate 4-line file "part.txt" (the ctor already seeded "f.txt").

    private static List<int> AtoAIndices(FilePatch file)
    {
        var lines = file.Hunks[0].Lines;
        var idx = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if ((l.Kind == DiffLineKind.Delete && l.Text == "a") || (l.Kind == DiffLineKind.Add && l.Text == "A"))
                idx.Add(i);
        }
        return idx;
    }

    // Locates a single-line change (oldText -> newText) within whichever hunk contains it,
    // returning the hunk index and the delete+add line indices to select. Line subsets only
    // reverse cleanly for changes surrounded by genuine (unchanged) context — as here.
    private static (int Hunk, List<int> Lines) FindChange(FilePatch file, string oldText, string newText)
    {
        for (int h = 0; h < file.Hunks.Count; h++)
        {
            var lines = file.Hunks[h].Lines;
            var sel = new List<int>();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if ((l.Kind == DiffLineKind.Delete && l.Text == oldText) || (l.Kind == DiffLineKind.Add && l.Text == newText))
                    sel.Add(i);
            }
            if (sel.Count > 0) return (h, sel);
        }
        return (-1, new List<int>());
    }

    [Fact]
    public void StageBuiltLinePatch_ShouldPutExactlySelectedLinesInIndex()
    {
        _fx.CommitFile("part.txt", "a\nb\nc\nd\n", "part base");
        _fx.WriteFile("part.txt", "A\nB\nC\nD\n");

        var file = PatchParser.Parse(_service.GetFileDiff(_fx.RepoPath, "part.txt", isStaged: false)).Single();
        var patch = PatchBuilder.BuildLinePatch(file, 0, AtoAIndices(file));

        _service.StageHunk(_fx.RepoPath, patch);

        var stagedLines = PatchParser.Parse(_service.GetFileDiff(_fx.RepoPath, "part.txt", isStaged: true))
            .Single().Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(stagedLines, l => l.Kind == DiffLineKind.Add && l.Text == "A");
        Assert.Contains(stagedLines, l => l.Kind == DiffLineKind.Delete && l.Text == "a");
        Assert.DoesNotContain(stagedLines, l => l.Kind == DiffLineKind.Add && (l.Text == "B" || l.Text == "C" || l.Text == "D"));

        // Working tree untouched — all four lines still changed on disk.
        Assert.Equal("A\nB\nC\nD\n", File.ReadAllText(Path.Combine(_fx.RepoPath, "part.txt")));
    }

    [Fact]
    public void UnstageBuiltPatch_ShouldReverseExactly()
    {
        // Two well-separated single-line edits (L2, L11) staged; unstage only L2's line.
        ModifyBothRegions();
        _service.StageFile(_fx.RepoPath, "f.txt");

        // Direction rule (invariant 4): unstage subsets come from the index<->HEAD diff.
        var file = PatchParser.Parse(_service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true)).Single();
        var (hunk, lines) = FindChange(file, "L2", "L2-changed");
        var patch = PatchBuilder.BuildLinePatch(file, hunk, lines);

        _service.UnstageHunk(_fx.RepoPath, patch);

        var stagedLines = PatchParser.Parse(_service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: true))
            .Single().Hunks.SelectMany(h => h.Lines).ToList();
        Assert.DoesNotContain(stagedLines, l => l.Kind == DiffLineKind.Add && l.Text == "L2-changed");
        Assert.Contains(stagedLines, l => l.Kind == DiffLineKind.Add && l.Text == "L11-changed");
    }

    [Fact]
    public void DiscardBuiltPatch_ShouldRemoveOnlySelectedLines_FromWorkdir()
    {
        ModifyBothRegions();

        var file = PatchParser.Parse(_service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: false)).Single();
        var (hunk, lines) = FindChange(file, "L2", "L2-changed");
        var patch = PatchBuilder.BuildLinePatch(file, hunk, lines);

        _service.DiscardHunk(_fx.RepoPath, patch);

        var content = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt"));
        Assert.Contains("L2\n", content);            // L2 reverted
        Assert.DoesNotContain("L2-changed", content);
        Assert.Contains("L11-changed", content);     // other edit untouched
    }

    [Fact]
    public void StaleBuiltPatch_ShouldThrowTyped_NotSilentlyRecount()
    {
        ModifyBothRegions();

        var file = PatchParser.Parse(_service.GetFileDiff(_fx.RepoPath, "f.txt", isStaged: false)).Single();
        var (hunk, lines) = FindChange(file, "L2", "L2-changed");
        var patch = PatchBuilder.BuildLinePatch(file, hunk, lines);

        // The working tree changes out from under the built patch: the +L2-changed line it
        // reverse-applies against is gone, so DiscardHunk (git apply --reverse) must fail typed.
        var stale = File.ReadAllText(Path.Combine(_fx.RepoPath, "f.txt")).Replace("L2-changed", "L2-different");
        _fx.WriteFile("f.txt", stale);

        Assert.Throws<GitOperationException>(() => _service.DiscardHunk(_fx.RepoPath, patch));
    }
}
