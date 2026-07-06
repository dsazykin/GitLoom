using System;
using System.IO;
using System.Linq;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using GitLoom.Tests.Fakes;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// TI-13 (ViewModel, TI-00 pattern): whitespace mode disables partial staging; syntax preference
// persists; intra-line spans and image detection reach the DiffViewerViewModel.
public class DiffViewerViewModelDiffQualityTests
{
    private const string SampleDiff =
        "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,3 +1,3 @@\n a\n-cat\n+dog\n c\n";

    [Fact]
    public void PartialStagingActions_ShouldBeHidden_InWhitespaceIgnoredMode()
    {
        var fake = new FakeGitService
        {
            // -w mode collapses to nothing; normal mode returns a real one-word-change diff.
            GetFileDiffWhitespaceImpl = (_, _, _, ignoreWs) => ignoreWs ? "" : SampleDiff
        };
        var vm = new DiffViewerViewModel(fake, "/does/not/exist");
        vm.UpdateDiff(new GitFileStatus { FilePath = "f.txt", State = FileStatus.ModifiedInWorkdir });

        // Normal mode: partial staging is available and hunks are built.
        Assert.True(vm.PartialStagingAvailable);
        Assert.NotEmpty(vm.Hunks);

        // Ignore-whitespace mode: actions unavailable, no hunks.
        vm.IgnoreWhitespace = true;
        Assert.False(vm.PartialStagingAvailable);
        Assert.Empty(vm.Hunks);
    }

    [Fact]
    public void SyntaxHighlightDiffs_ShouldDefaultTrue_AndPersistOnToggle()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "gitloom-diffq-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var settings = new SettingsService(tmp);
            var vm = new DiffViewerViewModel(new FakeGitService(), "/nope", settings: settings);

            Assert.True(vm.SyntaxHighlightDiffs);

            vm.ToggleSyntaxHighlightingCommand.Execute(null);

            Assert.False(vm.SyntaxHighlightDiffs);
            Assert.False(settings.Current.SyntaxHighlightDiffs);
            // Reload from disk to prove it was written.
            Assert.False(new SettingsService(tmp).Current.SyntaxHighlightDiffs);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void UpdateDiff_ShouldPopulateIntraLineSpans_OnAModifiedWord()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("f.txt", "alpha\nthe cat sat\nomega\n", "seed");
        fx.WriteFile("f.txt", "alpha\nthe dog sat\nomega\n");

        var vm = new DiffViewerViewModel(git, fx.RepoPath);
        vm.UpdateDiff(new GitFileStatus { FilePath = "f.txt", State = FileStatus.ModifiedInWorkdir });

        var changeLines = vm.Hunks.SelectMany(h => h.Lines).Where(l => l.IsChange).ToList();
        Assert.Contains(changeLines, l => l.HighlightSpans.Count > 0);

        // The emphasized run on the added line is exactly the changed word "dog".
        var addLine = changeLines.First(l => l.IsAdd && l.HighlightSpans.Count > 0);
        var span = addLine.HighlightSpans[0];
        Assert.Equal("dog", addLine.DisplayText.Substring(span.Start, span.Length));

        // And the side-by-side rows carry spans too.
        Assert.Contains(vm.Hunks.SelectMany(h => h.SideRows),
            r => r.RightLine.HighlightSpans.Count > 0 || r.LeftLine.HighlightSpans.Count > 0);
    }

    [Fact]
    public void UpdateDiff_ShouldFlagTrailingWhitespace_OnAnAddedLine()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("f.txt", "one\ntwo\n", "seed");
        fx.WriteFile("f.txt", "one\ntwo\nthree   \n"); // added line with trailing spaces

        var vm = new DiffViewerViewModel(git, fx.RepoPath);
        vm.UpdateDiff(new GitFileStatus { FilePath = "f.txt", State = FileStatus.ModifiedInWorkdir });

        var added = vm.Hunks.SelectMany(h => h.Lines).First(l => l.IsAdd && l.DisplayText.Contains("three"));
        Assert.NotNull(added.TrailingWhitespaceSpan);
    }

    [Fact]
    public void UpdateDiff_ShouldEnterImageMode_ForModifiedPngBlob()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        // A tiny "binary" payload with NUL bytes under an image extension.
        var original = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x00, 0x01, 0x02, 0x03 };
        File.WriteAllBytes(Path.Combine(fx.RepoPath, "logo.png"), original);
        using (var repo = new LibGit2Sharp.Repository(fx.RepoPath))
        {
            Commands.Stage(repo, "logo.png");
            var sig = new Signature("t", "t@t", DateTimeOffset.Now);
            repo.Commit("add image", sig, sig);
        }
        File.WriteAllBytes(Path.Combine(fx.RepoPath, "logo.png"),
            new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x00, 0x09, 0x09, 0x09, 0x09, 0x09 });

        var vm = new DiffViewerViewModel(git, fx.RepoPath);
        vm.UpdateDiff(new GitFileStatus { FilePath = "logo.png", State = FileStatus.ModifiedInWorkdir });

        Assert.True(vm.IsImageDiff);
        Assert.False(vm.IsBinaryDiff);
        Assert.False(vm.ShowUnified);      // text views suppressed
        Assert.Equal(8, vm.ImageDiff.OldSize);
        Assert.Equal(10, vm.ImageDiff.NewSize);
    }
}
