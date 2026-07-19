using System;
using System.Linq;
using Mainguard.Git.Exceptions;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;
using Repository = LibGit2Sharp.Repository;

namespace GitLoom.Tests;

// TI-05: tag management. CRUD + checkout via LibGit2Sharp; push/delete-remote against
// the T-01 bare-remote fixture with zero network. Assertions read real repository state.
public class GitServiceTagTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void CreateTag_Lightweight_ShouldAppearInGetTags_NotAnnotated()
    {
        var sha = _fx.CommitFile("a.txt", "hello\n", "init");

        _service.CreateTag(_fx.RepoPath, "v1", sha, message: null);

        var tag = Assert.Single(_service.GetTags(_fx.RepoPath));
        Assert.Equal("v1", tag.Name);
        Assert.Equal(sha, tag.TargetSha);
        Assert.False(tag.IsAnnotated);
        Assert.Null(tag.Message);
    }

    [Fact]
    public void CreateTag_Annotated_ShouldCarryMessageAndTagger_AndPeelToTarget()
    {
        var older = _fx.CommitFile("a.txt", "one\n", "c1");
        _fx.CommitFile("a.txt", "two\n", "c2"); // HEAD advances past the tagged commit

        _service.CreateTag(_fx.RepoPath, "release-1", older, "shipping release 1");

        var tag = Assert.Single(_service.GetTags(_fx.RepoPath));
        Assert.True(tag.IsAnnotated);
        // libgit2 canonicalizes annotation messages with a trailing newline.
        Assert.Equal("shipping release 1", tag.Message?.Trim());
        Assert.Equal("test-user", tag.TaggerName);
        // Peels to the tagged COMMIT, not the tag object, even though HEAD moved on.
        Assert.Equal(older, tag.TargetSha);
    }

    [Theory]
    [InlineData("a b")]
    [InlineData("-x")]
    [InlineData("a..b")]
    [InlineData("")]
    public void CreateTag_ShouldThrowTyped_OnInvalidName(string badName)
    {
        var sha = _fx.CommitFile("a.txt", "hello\n", "init");

        Assert.Throws<GitOperationException>(() =>
            _service.CreateTag(_fx.RepoPath, badName, sha, message: null));

        // Repo never left with a half-created ref.
        Assert.Empty(_service.GetTags(_fx.RepoPath));
    }

    [Fact]
    public void CreateTag_ShouldThrowTyped_OnDuplicateName()
    {
        var sha = _fx.CommitFile("a.txt", "hello\n", "init");
        _service.CreateTag(_fx.RepoPath, "v1", sha, message: null);

        Assert.Throws<GitOperationException>(() =>
            _service.CreateTag(_fx.RepoPath, "v1", sha, "different"));

        // Original intact and untouched (still lightweight).
        var tag = Assert.Single(_service.GetTags(_fx.RepoPath));
        Assert.False(tag.IsAnnotated);
    }

    [Fact]
    public void DeleteTag_ShouldRemove_AndThrowTypedWhenMissing()
    {
        var sha = _fx.CommitFile("a.txt", "hello\n", "init");
        _service.CreateTag(_fx.RepoPath, "v1", sha, message: null);

        _service.DeleteTag(_fx.RepoPath, "v1");
        Assert.Empty(_service.GetTags(_fx.RepoPath));

        Assert.Throws<GitOperationException>(() => _service.DeleteTag(_fx.RepoPath, "nope"));
    }

    [Fact]
    public void PushTag_ShouldCreateRemoteRef_OnBareRemote()
    {
        var sha = _fx.CommitFile("a.txt", "hello\n", "init");
        var barePath = _fx.AddBareRemote();
        _service.CreateTag(_fx.RepoPath, "v1", sha, message: null);

        _service.PushTag(_fx.RepoPath, "origin", "v1");

        using var bare = new Repository(barePath);
        Assert.NotNull(bare.Tags["v1"]);
    }

    [Fact]
    public void DeleteRemoteTag_ShouldRemoveRemoteRef_KeepLocal()
    {
        var sha = _fx.CommitFile("a.txt", "hello\n", "init");
        var barePath = _fx.AddBareRemote();
        _service.CreateTag(_fx.RepoPath, "v1", sha, message: null);
        _service.PushTag(_fx.RepoPath, "origin", "v1");

        _service.DeleteRemoteTag(_fx.RepoPath, "origin", "v1");

        using (var bare = new Repository(barePath))
        {
            Assert.Null(bare.Tags["v1"]); // remote ref gone
        }
        Assert.Single(_service.GetTags(_fx.RepoPath)); // local tag untouched
    }

    [Fact]
    public void CheckoutTag_ShouldDetachHead_AtPeeledTarget()
    {
        var older = _fx.CommitFile("a.txt", "one\n", "c1");
        _fx.CommitFile("a.txt", "two\n", "c2");
        _service.CreateTag(_fx.RepoPath, "v1", older, "annotated");

        _service.CheckoutTag(_fx.RepoPath, "v1");

        using var repo = new Repository(_fx.RepoPath);
        Assert.True(repo.Info.IsHeadDetached);
        Assert.Equal(older, repo.Head.Tip.Sha);
    }
}
