using System;
using System.IO;
using System.Linq;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

// TI-10: remotes management + push options. CRUD runs through LibGit2Sharp; the
// push-option paths (force-with-lease / set-upstream / tags) shell out to the git
// CLI, so those cases carry the RequiresGitCli trait. Every remote is a local bare
// repo (or a clone of one) — zero network.
public class GitServiceRemoteTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    private static string TipOf(string barePath, string branch)
    {
        using var bare = new Repository(barePath);
        return bare.Refs[$"refs/heads/{branch}"]!.ResolveToDirectReference().TargetIdentifier;
    }

    private static string CurrentBranch(string repoPath)
    {
        using var repo = new Repository(repoPath);
        return repo.Head.FriendlyName;
    }

    // 1 — CRUD round-trip; duplicate/missing throw typed.
    [Fact]
    public void Remotes_CrudRoundTrip()
    {
        _fx.CommitFile("a.txt", "hi\n", "init");

        Assert.Empty(_service.GetRemotes(_fx.RepoPath));

        _service.AddRemote(_fx.RepoPath, "origin", "https://example.com/o.git");
        _service.AddRemote(_fx.RepoPath, "upstream", "https://example.com/u.git");

        var remotes = _service.GetRemotes(_fx.RepoPath);
        Assert.Equal(2, remotes.Count);
        var origin = remotes.Single(r => r.Name == "origin");
        Assert.Equal("https://example.com/o.git", origin.FetchUrl);
        Assert.Null(origin.PushUrl); // push URL == fetch URL -> surfaced as null

        _service.RenameRemote(_fx.RepoPath, "upstream", "fork");
        Assert.Contains(_service.GetRemotes(_fx.RepoPath), r => r.Name == "fork");
        Assert.DoesNotContain(_service.GetRemotes(_fx.RepoPath), r => r.Name == "upstream");

        _service.RemoveRemote(_fx.RepoPath, "fork");
        Assert.Single(_service.GetRemotes(_fx.RepoPath));

        // Duplicate add -> typed.
        Assert.Throws<GitOperationException>(() =>
            _service.AddRemote(_fx.RepoPath, "origin", "https://example.com/x.git"));
        // Missing remove/rename -> typed RemoteNotFoundException.
        Assert.Throws<RemoteNotFoundException>(() => _service.RemoveRemote(_fx.RepoPath, "nope"));
        Assert.Throws<RemoteNotFoundException>(() => _service.RenameRemote(_fx.RepoPath, "nope", "other"));
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("bad..name")]
    [InlineData("-x")]
    [InlineData("")]
    public void AddRemote_ShouldThrowTyped_OnInvalidName(string badName)
    {
        _fx.CommitFile("a.txt", "hi\n", "init");
        Assert.Throws<GitOperationException>(() =>
            _service.AddRemote(_fx.RepoPath, badName, "https://example.com/o.git"));
        Assert.Empty(_service.GetRemotes(_fx.RepoPath)); // never half-created
    }

    [Fact]
    public void AddRemote_ShouldThrowTyped_OnEmptyUrl()
    {
        _fx.CommitFile("a.txt", "hi\n", "init");
        Assert.Throws<GitOperationException>(() => _service.AddRemote(_fx.RepoPath, "origin", "  "));
        Assert.Empty(_service.GetRemotes(_fx.RepoPath));
    }

    [Fact]
    public void GetRemotes_ShouldSurfaceDistinctPushUrl()
    {
        _fx.CommitFile("a.txt", "hi\n", "init");
        _service.AddRemote(_fx.RepoPath, "origin", "https://example.com/fetch.git");
        using (var repo = new Repository(_fx.RepoPath))
            repo.Network.Remotes.Update("origin", r => r.PushUrl = "https://example.com/push.git");

        var origin = Assert.Single(_service.GetRemotes(_fx.RepoPath));
        Assert.Equal("https://example.com/fetch.git", origin.FetchUrl);
        Assert.Equal("https://example.com/push.git", origin.PushUrl);
    }

    [Fact]
    public void SetRemoteUrl_ShouldUpdateFetchUrl_AndThrowTypedWhenMissing()
    {
        _fx.CommitFile("a.txt", "hi\n", "init");
        _service.AddRemote(_fx.RepoPath, "origin", "https://old.example.com/o.git");

        _service.SetRemoteUrl(_fx.RepoPath, "origin", "https://new.example.com/o.git");
        Assert.Equal("https://new.example.com/o.git",
            _service.GetRemotes(_fx.RepoPath).Single(r => r.Name == "origin").FetchUrl);

        Assert.Throws<RemoteNotFoundException>(() =>
            _service.SetRemoteUrl(_fx.RepoPath, "nope", "https://x"));
        Assert.Throws<GitOperationException>(() =>
            _service.SetRemoteUrl(_fx.RepoPath, "origin", "   "));
    }

    [Fact]
    public void GetDefaultRemoteName_ShouldPreferTracked_ThenOrigin_ThenSole_ElseThrow()
    {
        _fx.CommitFile("a.txt", "hi\n", "init");
        Assert.Throws<RemoteNotFoundException>(() => _service.GetDefaultRemoteName(_fx.RepoPath));

        _service.AddRemote(_fx.RepoPath, "solo", "https://example.com/s.git");
        Assert.Equal("solo", _service.GetDefaultRemoteName(_fx.RepoPath)); // sole remote

        _service.AddRemote(_fx.RepoPath, "origin", "https://example.com/o.git");
        Assert.Equal("origin", _service.GetDefaultRemoteName(_fx.RepoPath)); // origin wins over other non-tracked
    }

    // 2 — tracked branch on `upstream` -> push/fetch target upstream, not origin.
    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void Push_ShouldUseTrackedRemote_NotOrigin()
    {
        var baseSha = _fx.CommitFile("a.txt", "base\n", "init");
        var originBare = _fx.AddBareRemote("origin");
        var upstreamBare = _fx.AddBareRemote("upstream");
        var branch = CurrentBranch(_fx.RepoPath);
        _fx.SetUpstream("upstream");

        var newSha = _fx.CommitFile("a.txt", "changed\n", "local change");

        _service.Push(_fx.RepoPath);

        Assert.Equal(newSha, TipOf(upstreamBare, branch)); // landed on upstream
        Assert.Equal(baseSha, TipOf(originBare, branch));  // origin untouched
    }

    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void Fetch_ByRemoteName_ShouldUpdateThatRemotesTrackingRef()
    {
        _fx.CommitFile("a.txt", "base\n", "init");
        var originBare = _fx.AddBareRemote("origin");
        var clone = _fx.CloneBare(originBare);

        // Move origin from a second clone, then fetch it by name from the first.
        var mover = _fx.CloneBare(originBare);
        var moved = TempRepoFixture.CommitInto(mover, "a.txt", "moved\n", "move");
        _service.Push(mover);

        _service.Fetch(clone, "origin", prune: true);

        using var repo = new Repository(clone);
        Assert.Equal(moved, repo.Refs["refs/remotes/origin/" + CurrentBranch(clone)]!
            .ResolveToDirectReference().TargetIdentifier);
    }

    // 3 — zero remotes -> typed RemoteNotFoundException on every remote op.
    [Fact]
    public void Operations_ShouldThrowRemoteNotFound_WithZeroRemotes()
    {
        _fx.CommitFile("a.txt", "hi\n", "init");
        Assert.Throws<RemoteNotFoundException>(() => _service.Push(_fx.RepoPath));
        Assert.Throws<RemoteNotFoundException>(() => _service.Fetch(_fx.RepoPath));
        Assert.Throws<RemoteNotFoundException>(() => _service.PushBranch(_fx.RepoPath, CurrentBranch(_fx.RepoPath)));
    }

    // 4 — force-with-lease after local amend, remote unmoved -> succeeds.
    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void PushForceWithLease_ShouldSucceed_WhenRemoteUnmoved()
    {
        _fx.CommitFile("a.txt", "base\n", "init");
        var bare = _fx.AddBareRemote("origin");
        var clone = _fx.CloneBare(bare);
        var branch = CurrentBranch(clone);

        // Rewrite local history (amend) so a fast-forward push is impossible.
        string head;
        using (var repo = new Repository(clone)) head = repo.Head.Tip.Sha;
        _service.AmendCommitMessage(clone, head, "amended message");

        _service.PushForceWithLease(clone, "origin", branch);

        string localTip;
        using (var repo = new Repository(clone)) localTip = repo.Head.Tip.Sha;
        Assert.Equal(localTip, TipOf(bare, branch)); // lease held; remote now matches local
    }

    // 5 — force-with-lease when the remote moved (2nd clone pushed first) -> fails typed.
    //     The safety property. Never skipped.
    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void PushForceWithLease_ShouldThrowTyped_WhenRemoteMoved()
    {
        _fx.CommitFile("a.txt", "base\n", "init");
        var bare = _fx.AddBareRemote("origin");
        var clone = _fx.CloneBare(bare);
        var branch = CurrentBranch(clone);

        // A second clone pushes first, advancing origin behind our back.
        var other = _fx.CloneBare(bare);
        var movedTip = TempRepoFixture.CommitInto(other, "a.txt", "other work\n", "other");
        _service.Push(other);

        // We amend locally without fetching -> our lease ref (origin/<branch>) is stale.
        string head;
        using (var repo = new Repository(clone)) head = repo.Head.Tip.Sha;
        _service.AmendCommitMessage(clone, head, "amended");

        // A real push rejection surfaces as GitOperationException — not the auth type
        // (AuthenticationRequiredException is a sibling, so Assert.Throws already rules it out).
        Assert.Throws<GitOperationException>(() =>
            _service.PushForceWithLease(clone, "origin", branch));

        // Remote untouched: the other clone's work is still there (nothing clobbered).
        Assert.Equal(movedTip, TipOf(bare, branch));
    }

    // 6 — `-u` push writes branch.<name>.remote + .merge config.
    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void PushSetUpstream_ShouldWriteBranchConfig()
    {
        _fx.CommitFile("a.txt", "base\n", "init");
        var bare = _fx.AddBareRemote("origin");
        var clone = _fx.CloneBare(bare);

        using (var repo = new Repository(clone))
            repo.CreateBranch("feature");
        TempRepoFixture.CommitInto(clone, "b.txt", "feature\n", "feature work"); // on current branch
        // Check out feature so -u tracks it.
        using (var repo = new Repository(clone))
            Commands.Checkout(repo, repo.Branches["feature"]);

        _service.PushSetUpstream(clone, "origin", "feature");

        using (var repo = new Repository(clone))
        {
            var feature = repo.Branches["feature"];
            Assert.NotNull(feature.TrackedBranch);
            Assert.Equal("origin", feature.RemoteName);
            Assert.Equal("origin/feature", feature.TrackedBranch.FriendlyName);
        }
    }

    // Tags push option — lands the tag ref on the bare via the CLI path.
    [Fact]
    [Trait("Category", "RequiresGitCli")]
    public void PushTags_ShouldLandTagRefs_OnRemote()
    {
        var sha = _fx.CommitFile("a.txt", "hi\n", "init");
        var bare = _fx.AddBareRemote("origin");
        _service.CreateTag(_fx.RepoPath, "v1", sha, "release one");

        _service.PushTags(_fx.RepoPath, "origin");

        using var repo = new Repository(bare);
        Assert.NotNull(repo.Tags["v1"]);
    }
}
