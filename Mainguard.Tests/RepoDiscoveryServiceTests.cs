using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// PR2 — pins <see cref="RepoDiscoveryService"/>, the service form of the sidebar's auto-detect
/// folder scan the OOBE repo-onboarding step reuses: root-is-a-repo shortcut, the two-level walk
/// (top-level dirs + one grouping level down), non-repos skipped, sorted stable output, and the
/// never-throws contract for missing/unreadable roots. Uses real git repos in a temp tree.
/// </summary>
public sealed class RepoDiscoveryServiceTests : IDisposable
{
    private readonly string _root;
    private readonly RepoDiscoveryService _discovery = new(new GitService());

    public RepoDiscoveryServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gitloom-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            // .git objects are read-only on Windows; strip the attribute before deleting.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Temp cleanup is best-effort.
        }
    }

    private string InitRepo(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        LibGit2Sharp.Repository.Init(path);
        return path;
    }

    private string PlainDir(params string[] segments)
    {
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RootThatIsItselfARepo_IsReturnedAsTheSingleResult()
    {
        var repo = InitRepo("solo");

        var found = _discovery.DiscoverRepositories(repo);

        Assert.Equal(new[] { repo }, found);
    }

    [Fact]
    public void ScansTopLevelAndOneGroupingLevelDown_SkippingNonRepos()
    {
        var topRepo = InitRepo("alpha");
        var nestedRepo = InitRepo("client-work", "beta");
        PlainDir("not-a-repo");
        PlainDir("client-work", "notes"); // non-repo grandchild — skipped

        var found = _discovery.DiscoverRepositories(_root);

        Assert.Equal(2, found.Count);
        Assert.Contains(topRepo, found);
        Assert.Contains(nestedRepo, found);
        // Path-sorted for a stable list.
        Assert.Equal(found.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), found);
    }

    [Fact]
    public void ThirdLevelRepos_AreOutOfScope_LikeTheSidebarScan()
    {
        InitRepo("group", "subgroup", "too-deep");

        var found = _discovery.DiscoverRepositories(_root);

        Assert.Empty(found);
    }

    [Fact]
    public void MissingOrBlankRoot_YieldsEmpty_NeverThrows()
    {
        Assert.Empty(_discovery.DiscoverRepositories(Path.Combine(_root, "does-not-exist")));
        Assert.Empty(_discovery.DiscoverRepositories(string.Empty));
        Assert.Empty(_discovery.DiscoverRepositories("   "));
    }

    [Fact]
    public void IsGitRepository_Passthrough_MatchesGitService()
    {
        var repo = InitRepo("real");
        var plain = PlainDir("plain");

        Assert.True(_discovery.IsGitRepository(repo));
        Assert.False(_discovery.IsGitRepository(plain));
        Assert.False(_discovery.IsGitRepository(string.Empty));
    }
}
