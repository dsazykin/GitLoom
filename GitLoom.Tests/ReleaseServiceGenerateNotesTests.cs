using System;
using System.IO;
using Mainguard.Git.Security;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-28 (local notes) — drives <see cref="ReleaseService.GenerateNotes"/> over a real fixture repo (no
/// network): notes cover only the commits since the previous release tag (highest semver-ish reachable),
/// fall back to the whole history when there is no prior tag, and yield empty notes on an empty repo.
/// </summary>
public class ReleaseServiceGenerateNotesTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();

    public void Dispose() => _fx.Dispose();

    private ReleaseService NewService()
    {
        var keyringDir = Path.Combine(Path.GetTempPath(), "gitloom-release-keyring-" + Guid.NewGuid().ToString("N"));
        return new ReleaseService(_git, new SecureKeyring(keyringDir));
    }

    [Fact]
    public void GenerateNotes_SincePreviousTag_ExcludesTaggedAndOlder()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "feat: initial feature");
        _git.CreateTag(_fx.RepoPath, "v1.0.0", c1, message: null);
        _fx.CommitFile("b.txt", "2\n", "fix(core): stop the crash");
        _fx.CommitFile("c.txt", "3\n", "feat(ui): shiny button");

        var notes = NewService().GenerateNotes(_fx.RepoPath, "v1.1.0", "HEAD");

        Assert.Contains("### Fixes", notes);
        Assert.Contains("stop the crash", notes);
        Assert.Contains("### Features", notes);
        Assert.Contains("shiny button", notes);
        Assert.DoesNotContain("initial feature", notes); // the tagged commit is the boundary, excluded
        Assert.Contains("**Full changelog:** v1.0.0...v1.1.0", notes);
    }

    [Fact]
    public void GenerateNotes_PicksHighestSemverTag_AsPrevious()
    {
        var c1 = _fx.CommitFile("a.txt", "1\n", "feat: one");
        _git.CreateTag(_fx.RepoPath, "v1.0.0", c1, message: null);
        var c2 = _fx.CommitFile("b.txt", "2\n", "feat: two");
        _git.CreateTag(_fx.RepoPath, "v1.1.0", c2, message: null);
        _fx.CommitFile("c.txt", "3\n", "fix: three");

        var notes = NewService().GenerateNotes(_fx.RepoPath, "v2.0.0", "HEAD");

        // Previous release is the highest reachable tag (v1.1.0), so only the post-v1.1.0 commit is in scope.
        Assert.Contains("three", notes);
        Assert.DoesNotContain("feat: one", notes);
        Assert.DoesNotContain("feat: two", notes);
        Assert.Contains("**Full changelog:** v1.1.0...v2.0.0", notes);
    }

    [Fact]
    public void GenerateNotes_NoPreviousTag_CoversWholeHistory()
    {
        _fx.CommitFile("a.txt", "1\n", "feat: alpha");
        _fx.CommitFile("b.txt", "2\n", "fix: beta");

        var notes = NewService().GenerateNotes(_fx.RepoPath, "v1.0.0", "HEAD");

        Assert.Contains("alpha", notes);
        Assert.Contains("beta", notes);
        Assert.Contains("**Full changelog:** v1.0.0", notes);
        Assert.DoesNotContain("...", notes); // no range when there's no previous tag
    }

    [Fact]
    public void GenerateNotes_EmptyRepo_ReturnsEmpty_NoThrow()
    {
        // TempRepoFixture inits a repo but commits nothing here → unborn HEAD.
        var notes = NewService().GenerateNotes(_fx.RepoPath, "v1.0.0", "HEAD");
        Assert.Equal("", notes);
    }

    [Fact]
    public void GenerateNotes_NonConventionalSubjects_GroupedUnderOther()
    {
        _fx.CommitFile("a.txt", "1\n", "Just a plain commit message");

        var notes = NewService().GenerateNotes(_fx.RepoPath, "v1.0.0", "HEAD");

        Assert.Contains("### Other", notes);
        Assert.Contains("Just a plain commit message", notes);
    }
}
