using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-12 ViewModel behavior for the file-history dialog: history load + auto-select newest, the
/// selection→predecessor-diff rendering, the introducing-revision (all-additions) render, the binary
/// placeholder, and the line-history filter narrowing. Driven by <see cref="FakeGitService"/> so the
/// tests are pure/deterministic (no repo).
/// </summary>
public class FileHistoryViewModelTests
{
    private static FileVersion V(string sha, string path = "file.txt", int minute = 0) => new()
    {
        Sha = sha,
        PathAtCommit = path,
        MessageShort = $"commit {sha}",
        AuthorName = "author",
        When = new DateTimeOffset(2024, 1, 1, 12, minute, 0, TimeSpan.Zero),
    };

    private static async Task<T> Settle<T>(Func<T> read, Func<bool> until)
    {
        for (int i = 0; i < 200 && !until(); i++) await Task.Delay(5);
        return read();
    }

    [Fact]
    public async Task LoadAsync_ShouldPopulateNewestFirst_AndSelectNewest()
    {
        var history = new List<FileVersion> { V("c3", minute: 3), V("c2", minute: 2), V("c1", minute: 1) };
        var fake = new FakeGitService
        {
            GetFileHistoryImpl = (_, _) => history,
            GetFileDiffBetweenCommitsImpl = (_, older, newer, _) => $"@@ -1 +1 @@\n-{older}\n+{newer}\n",
        };
        var vm = new FileHistoryViewModel(fake, "/repo", "file.txt");

        await vm.LoadAsync();
        await Settle(() => vm.DiffLines.Count, () => vm.DiffLines.Count > 0);

        Assert.Equal(3, vm.Versions.Count);
        Assert.Equal("c3", vm.Versions[0].Sha);
        Assert.Equal("c3", vm.SelectedVersion!.Sha);   // newest auto-selected
        Assert.Equal("3 revisions", vm.VersionCountText);
        // Diff of the newest (c3) is against its predecessor c2.
        Assert.Contains(vm.DiffLines, l => l.Content.Contains("+c3"));
        Assert.Contains(vm.DiffLines, l => l.Content.Contains("-c2"));
        Assert.True(vm.HasDiffLines);
    }

    [Fact]
    public async Task Selecting_OldestRevision_ShouldRenderIntroductionAsAllAdditions()
    {
        var history = new List<FileVersion> { V("c2", minute: 2), V("c1", minute: 1) };
        var fake = new FakeGitService
        {
            GetFileHistoryImpl = (_, _) => history,
            GetFileDiffBetweenCommitsImpl = (_, _, _, _) => "@@ -1 +1 @@\n-x\n+y\n",
            GetFileAtCommitImpl = (_, sha, _) => "alpha\nbeta\ngamma\n",   // introduced content
        };
        var vm = new FileHistoryViewModel(fake, "/repo", "file.txt");
        await vm.LoadAsync();

        vm.SelectedVersion = history.Last();   // the introducing revision (c1)
        await Settle(() => vm.DiffLines.Count, () => vm.DiffLines.Any(l => l.Content.Contains("+alpha")));

        Assert.True(vm.HasDiffLines);
        Assert.Contains(vm.DiffLines, l => l.Content == "+alpha");
        Assert.Contains(vm.DiffLines, l => l.Content == "+beta");
        Assert.Contains(vm.DiffLines, l => l.Content == "+gamma");
        Assert.DoesNotContain(vm.DiffLines, l => l.IsRemoved);   // introduction has no deletions
    }

    [Fact]
    public async Task BinaryIntroduction_ShouldShowPlaceholder_NotGarbage()
    {
        var history = new List<FileVersion> { V("c1", minute: 1) };   // single revision = introduction
        var fake = new FakeGitService
        {
            GetFileHistoryImpl = (_, _) => history,
            GetFileAtCommitImpl = (_, _, _) => throw new GitOperationException("'file.bin' is a binary file at c1."),
        };
        var vm = new FileHistoryViewModel(fake, "/repo", "file.bin");

        await vm.LoadAsync();
        await Settle(() => vm.DiffPlaceholder, () => !string.IsNullOrEmpty(vm.DiffPlaceholder));

        Assert.False(vm.HasDiffLines);
        Assert.Empty(vm.DiffLines);
        Assert.Contains("binary", vm.DiffPlaceholder!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BinaryDiff_BetweenCommits_ShouldShowPlaceholder()
    {
        var history = new List<FileVersion> { V("c2", minute: 2), V("c1", minute: 1) };
        var fake = new FakeGitService
        {
            GetFileHistoryImpl = (_, _) => history,
            GetFileDiffBetweenCommitsImpl = (_, _, _, _) =>
                "diff --git a/file.bin b/file.bin\nBinary files a/file.bin and b/file.bin differ\n",
        };
        var vm = new FileHistoryViewModel(fake, "/repo", "file.bin");

        await vm.LoadAsync();   // newest (c2) vs c1 is a binary diff
        await Settle(() => vm.DiffPlaceholder, () => !string.IsNullOrEmpty(vm.DiffPlaceholder));

        Assert.False(vm.HasDiffLines);
        Assert.Contains("Binary", vm.DiffPlaceholder!);
    }

    [Fact]
    public async Task ApplyLineFilter_ShouldNarrowToIntersectingRevisions()
    {
        var history = new List<FileVersion> { V("c3", minute: 3), V("c2", minute: 2), V("c1", minute: 1) };
        var fake = new FakeGitService
        {
            GetFileHistoryImpl = (_, _) => history,
            // c3 touches line 11 (in range); c2 touches line 40 (out); c1 is the introduction.
            GetFileDiffBetweenCommitsImpl = (_, older, newer, _) => newer switch
            {
                "c3" => "@@ -11,1 +11,1 @@\n-old\n+new\n",
                "c2" => "@@ -40,1 +40,1 @@\n-x\n+y\n",
                _ => "",
            },
            GetFileAtCommitImpl = (_, _, _) => "l1\nl2\n",   // introduction: 2 lines, outside 10–12
        };
        var vm = new FileHistoryViewModel(fake, "/repo", "file.txt");
        await vm.LoadAsync();

        vm.LineRangeStart = "10";
        vm.LineRangeEnd = "12";
        await vm.ApplyLineFilterCommand.ExecuteAsync(null);

        Assert.True(vm.IsLineFilterActive);
        Assert.Equal(new[] { "c3" }, vm.Versions.Select(v => v.Sha).ToArray());
        Assert.Contains("git log -L", vm.LineFilterSummary);

        vm.ClearLineFilterCommand.Execute(null);
        Assert.False(vm.IsLineFilterActive);
        Assert.Equal(3, vm.Versions.Count);
    }

    [Fact]
    public async Task ApplyLineFilter_InvalidRange_ShouldSurfaceError_AndKeepList()
    {
        var history = new List<FileVersion> { V("c1", minute: 1) };
        var fake = new FakeGitService { GetFileHistoryImpl = (_, _) => history, GetFileAtCommitImpl = (_, _, _) => "a\n" };
        var vm = new FileHistoryViewModel(fake, "/repo", "file.txt");
        await vm.LoadAsync();

        vm.LineRangeStart = "not-a-number";
        await vm.ApplyLineFilterCommand.ExecuteAsync(null);

        Assert.False(vm.IsLineFilterActive);
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.Single(vm.Versions);
    }

    [Fact]
    public async Task LoadAsync_EmptyHistory_ShouldShowPlaceholder()
    {
        var fake = new FakeGitService { GetFileHistoryImpl = (_, _) => new List<FileVersion>() };
        var vm = new FileHistoryViewModel(fake, "/repo", "untracked.txt");

        await vm.LoadAsync();

        Assert.Empty(vm.Versions);
        Assert.Null(vm.SelectedVersion);
        Assert.False(vm.HasDiffLines);
        Assert.False(string.IsNullOrEmpty(vm.DiffPlaceholder));
    }

    [Fact]
    public async Task LoadAsync_ServiceThrows_ShouldSurfaceError()
    {
        var fake = new FakeGitService
        {
            GetFileHistoryImpl = (_, _) => throw new GitOperationException("boom"),
        };
        var vm = new FileHistoryViewModel(fake, "/repo", "file.txt");

        await vm.LoadAsync();

        Assert.Equal("boom", vm.ErrorMessage);
        Assert.False(vm.IsLoading);
    }
}
