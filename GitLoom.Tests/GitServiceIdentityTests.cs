using System;
using System.IO;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

// Backfill B-4 (test strategy doc): fix 1.2 routed EVERY mutating operation
// through GetSignature — the existing suite only proved it for Commit. These
// tests mutate LibGit2Sharp's process-global config search paths, so they live
// in the "GlobalGitConfig" collection to never run in parallel with each other
// or with other identity-sensitive tests.
[Collection("GlobalGitConfig")]
public class GitServiceIdentityTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _service = new();

    public void Dispose() => _fx.Dispose();

    /// <summary>
    /// Points every non-local config level at an empty directory (so the
    /// developer's real global gitconfig can't satisfy the identity lookup)
    /// and removes the fixture's local identity. Restores defaults on dispose.
    /// </summary>
    private sealed class NoIdentityScope : IDisposable
    {
        private readonly string _emptyDir;

        public NoIdentityScope(string repoPath)
        {
            using (var repo = new Repository(repoPath))
            {
                repo.Config.Unset("user.name", ConfigurationLevel.Local);
                repo.Config.Unset("user.email", ConfigurationLevel.Local);
            }

            _emptyDir = Path.Combine(Path.GetTempPath(), "GitLoomNoCfg_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_emptyDir);
            GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, _emptyDir);
            GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Xdg, _emptyDir);
            GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.System, _emptyDir);
        }

        public void Dispose()
        {
            GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Global, null);
            GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.Xdg, null);
            GlobalSettings.SetConfigSearchPaths(ConfigurationLevel.System, null);
            try { Directory.Delete(_emptyDir, true); } catch { }
        }
    }

    [Fact]
    public void RevertCommit_ShouldThrowGitIdentityMissing_WhenNoIdentity()
    {
        _fx.CommitFile("a.txt", "one\n", "first");
        var second = _fx.CommitFile("a.txt", "two\n", "second");

        using var scope = new NoIdentityScope(_fx.RepoPath);
        Assert.Throws<GitIdentityMissingException>(() => _service.RevertCommit(_fx.RepoPath, second));
    }

    [Fact]
    public void CherryPick_ShouldThrowGitIdentityMissing_WhenNoIdentity()
    {
        _fx.CommitFile("a.txt", "one\n", "first");
        var second = _fx.CommitFile("b.txt", "two\n", "second");

        using var scope = new NoIdentityScope(_fx.RepoPath);
        Assert.Throws<GitIdentityMissingException>(() => _service.CherryPick(_fx.RepoPath, second));
    }

    [Fact]
    public void StashPush_ShouldThrowGitIdentityMissing_WhenNoIdentity()
    {
        _fx.CommitFile("a.txt", "one\n", "first");
        _fx.WriteFile("a.txt", "dirty\n");

        using var scope = new NoIdentityScope(_fx.RepoPath);
        Assert.Throws<GitIdentityMissingException>(() => _service.StashPush(_fx.RepoPath, "wip"));
    }

    [Fact]
    public void AmendCommitMessage_ShouldThrowGitIdentityMissing_WhenNoIdentity()
    {
        var sha = _fx.CommitFile("a.txt", "one\n", "first");

        using var scope = new NoIdentityScope(_fx.RepoPath);
        Assert.Throws<GitIdentityMissingException>(
            () => _service.AmendCommitMessage(_fx.RepoPath, sha, "new message"));
    }
}
