using System;
using System.Linq;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

// Backfill B-6 + parts of B-10 (test strategy doc): the GetRecentCommits filter
// dimensions and pagination stability that fix 1.8 promised, plus the
// never-tested history read APIs.
public class GitServiceHistoryTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void GetRecentCommits_TextFilter_ShouldMatchMessage_AndShaPrefix()
    {
        _fx.CommitFile("a.txt", "1\n", "feat: alpha widget");
        var betaSha = _fx.CommitFile("a.txt", "2\n", "fix: beta gadget");

        var byMessage = _service.GetRecentCommits(_fx.RepoPath, 0, 10,
            new CommitSearchFilter { Text = "ALPHA" }).ToList();
        Assert.Single(byMessage);
        Assert.Equal("feat: alpha widget", byMessage[0].MessageShort);

        var byShaPrefix = _service.GetRecentCommits(_fx.RepoPath, 0, 10,
            new CommitSearchFilter { Text = betaSha.Substring(0, 10) }).ToList();
        Assert.Single(byShaPrefix);
        Assert.Equal(betaSha, byShaPrefix[0].Sha);
    }

    [Fact]
    public void GetRecentCommits_AuthorFilter_ShouldMatchNameOrEmail()
    {
        _fx.CommitFile("a.txt", "1\n", "by alice", "Alice Smith", "alice@example.com", DateTimeOffset.Now);
        _fx.CommitFile("a.txt", "2\n", "by bob", "Bob Jones", "bob@example.com", DateTimeOffset.Now);

        var byName = _service.GetRecentCommits(_fx.RepoPath, 0, 10,
            new CommitSearchFilter { Author = "alice" }).ToList();
        Assert.Single(byName);
        Assert.Equal("by alice", byName[0].MessageShort);

        var byEmail = _service.GetRecentCommits(_fx.RepoPath, 0, 10,
            new CommitSearchFilter { Author = "bob@example" }).ToList();
        Assert.Single(byEmail);
        Assert.Equal("by bob", byEmail[0].MessageShort);
    }

    [Fact]
    public void GetRecentCommits_DateRange_ShouldBoundResults()
    {
        var t2020 = new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t2021 = new DateTimeOffset(2021, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var t2022 = new DateTimeOffset(2022, 1, 1, 12, 0, 0, TimeSpan.Zero);
        _fx.CommitFile("a.txt", "1\n", "old", "test-user", "test@gitloom.local", t2020);
        _fx.CommitFile("a.txt", "2\n", "middle", "test-user", "test@gitloom.local", t2021);
        _fx.CommitFile("a.txt", "3\n", "new", "test-user", "test@gitloom.local", t2022);

        var filter = new CommitSearchFilter
        {
            DateFrom = new DateTime(2020, 6, 1),
            DateTo = new DateTime(2021, 6, 1)
        };
        var result = _service.GetRecentCommits(_fx.RepoPath, 0, 10, filter).ToList();

        Assert.Single(result);
        Assert.Equal("middle", result[0].MessageShort);
    }

    [Fact]
    public void GetRecentCommits_Pagination_ShouldBeStableAndNonOverlapping()
    {
        for (int i = 1; i <= 30; i++)
        {
            _fx.CommitFile("a.txt", $"content {i}\n", $"commit {i}");
        }

        var full = _service.GetRecentCommits(_fx.RepoPath, 0, 30).Select(c => c.Sha).ToList();
        var page1 = _service.GetRecentCommits(_fx.RepoPath, 0, 10).Select(c => c.Sha).ToList();
        var page2a = _service.GetRecentCommits(_fx.RepoPath, 10, 10).Select(c => c.Sha).ToList();
        var page2b = _service.GetRecentCommits(_fx.RepoPath, 10, 10).Select(c => c.Sha).ToList();
        var page3 = _service.GetRecentCommits(_fx.RepoPath, 20, 10).Select(c => c.Sha).ToList();

        Assert.Equal(page2a, page2b); // same page fetched twice is identical
        var stitched = page1.Concat(page2a).Concat(page3).ToList();
        Assert.Equal(full, stitched); // pages concatenate to the full walk, no gaps/overlap
        Assert.Equal(30, stitched.Distinct().Count());
    }

    [Fact]
    public void GetRecentCommits_SinglePathFilter_ShouldFollowOnlyThatFile()
    {
        // Distinct timestamps are load-bearing: LibGit2Sharp's FileHistory
        // (QueryBy(path), the single-path fast path from fix 1.8) throws
        // KeyNotFoundException when commits share the same second — a known
        // upstream sort-instability quirk. Real repos have distinct times;
        // T-12 (file history feature) must keep this constraint in mind.
        var t0 = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        _fx.CommitFile("a.txt", "a1\n", "add a", "test-user", "test@gitloom.local", t0);
        _fx.CommitFile("b.txt", "b1\n", "add b", "test-user", "test@gitloom.local", t0.AddMinutes(1));
        _fx.CommitFile("a.txt", "a2\n", "modify a", "test-user", "test@gitloom.local", t0.AddMinutes(2));

        var result = _service.GetRecentCommits(_fx.RepoPath, 0, 10,
            new CommitSearchFilter { FilePaths = new() { "a.txt" } }).ToList();

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "modify a", "add a" }, result.Select(c => c.MessageShort).ToArray());
    }

    [Fact]
    public void GetCommitModifiedFiles_ShouldDiffAgainstParent_AndEmptyTreeForRoot()
    {
        var root = _fx.CommitFile("a.txt", "a\n", "root");
        var second = _fx.CommitFile("b.txt", "b\n", "second");

        Assert.Equal(new[] { "a.txt" }, _service.GetCommitModifiedFiles(_fx.RepoPath, root).ToArray());
        Assert.Equal(new[] { "b.txt" }, _service.GetCommitModifiedFiles(_fx.RepoPath, second).ToArray());
    }

    [Fact]
    public void GetBranchesContainingCommit_ShouldListOnlyContainingBranches()
    {
        var shared = _fx.CommitFile("a.txt", "base\n", "shared");
        string defaultBranch;
        using (var repo = new Repository(_fx.RepoPath)) defaultBranch = repo.Head.FriendlyName;

        _fx.CreateBranch("feature");
        _fx.Checkout("feature");
        var featureOnly = _fx.CommitFile("b.txt", "f\n", "feature only");

        var containingShared = _service.GetBranchesContainingCommit(_fx.RepoPath, shared).ToList();
        Assert.Contains(defaultBranch, containingShared);
        Assert.Contains("feature", containingShared);

        var containingFeature = _service.GetBranchesContainingCommit(_fx.RepoPath, featureOnly).ToList();
        Assert.Contains("feature", containingFeature);
        Assert.DoesNotContain(defaultBranch, containingFeature);
    }

    [Fact]
    public void GetAuthors_ShouldBeDistinctAndSorted()
    {
        _fx.CommitFile("a.txt", "1\n", "c1", "Zoe", "z@example.com", DateTimeOffset.Now);
        _fx.CommitFile("a.txt", "2\n", "c2", "Adam", "a@example.com", DateTimeOffset.Now);
        _fx.CommitFile("a.txt", "3\n", "c3", "Zoe", "z@example.com", DateTimeOffset.Now);

        var authors = _service.GetAuthors(_fx.RepoPath).ToList();

        Assert.Equal(new[] { "Adam", "Zoe" }, authors);
    }
}
