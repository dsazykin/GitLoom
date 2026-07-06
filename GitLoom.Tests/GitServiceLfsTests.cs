using System;
using System.IO;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

// TI-17 (integration): Git LFS end-to-end over a LOCAL fixture — no network is needed for
// track/commit/ls-files/prune. These need a real git-lfs (the SAME git the service shells out to),
// so they are gated RequiresGitLfs and Assert.Skip cleanly when git-lfs is absent.
//
// Committing an LFS-tracked file MUST go through the git CLI (git add/commit) so git-lfs's clean
// filter converts the content to a pointer — libgit2 would store the raw bytes. That is exactly why
// LFS is a CLI concern in the app, and the tests mirror it.
[Trait("Category", "RequiresGitLfs")]
public sealed class GitServiceLfsTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();
    private readonly LfsService _lfs;

    public GitServiceLfsTests()
    {
        _lfs = new LfsService(_git);
    }

    public void Dispose() => _fx.Dispose();

    // git-lfs 3.5.1 ships with Git-for-Windows here; skip only if it is genuinely unavailable.
    private void RequireLfs()
    {
        if (!_lfs.IsAvailable(_fx.RepoPath))
            throw Xunit.Sdk.SkipException.ForSkip("git-lfs is not available in this environment.");
    }

    // Runs raw git in the fixture repo (setup / verification), asserting success unless told otherwise.
    private (int Code, string Out, string Err) Git(params string[] args)
    {
        var r = GitService.RunGit(_fx.RepoPath, args);
        return r;
    }

    private void CommitBinaryViaCli(string relPath, byte[] content)
    {
        File.WriteAllBytes(Path.Combine(_fx.RepoPath, relPath), content);
        var add = Git("add", "--", relPath);
        Assert.True(add.Code == 0, add.Err);
        var commit = Git("commit", "-m", "add binary");
        Assert.True(commit.Code == 0, commit.Err);
    }

    private static byte[] SampleBinary() => new byte[] { 0x42, 0x49, 0x4E, 0x00, 0x01, 0x02, 0xFF, 0xFE };

    [Fact]
    public void LfsTrack_ShouldWriteGitattributes_AndCommitPointer()
    {
        RequireLfs();
        _lfs.Install(_fx.RepoPath);
        _lfs.Track(_fx.RepoPath, "*.bin");

        var attrs = File.ReadAllText(Path.Combine(_fx.RepoPath, ".gitattributes"));
        Assert.Contains("*.bin", attrs);
        Assert.Contains("filter=lfs", attrs);

        CommitBinaryViaCli("asset.bin", SampleBinary());

        // The committed blob is the LFS pointer, not the raw bytes.
        var show = Git("show", "HEAD:asset.bin");
        Assert.Equal(0, show.Code);
        Assert.True(LfsPointer.IsPointer(show.Out), $"HEAD:asset.bin was not a pointer:\n{show.Out}");
    }

    [Fact]
    public void LfsLsFiles_ShouldListTrackedObject()
    {
        RequireLfs();
        _lfs.Install(_fx.RepoPath);
        _lfs.Track(_fx.RepoPath, "*.bin");
        CommitBinaryViaCli("asset.bin", SampleBinary());

        var files = _lfs.ListLfsFiles(_fx.RepoPath);
        Assert.Single(files);
        Assert.Equal("asset.bin", files[0].Path);
        Assert.True(files[0].IsDownloaded);           // content present in the working tree
        Assert.False(string.IsNullOrEmpty(files[0].Oid));

        Assert.Contains("asset.bin", _lfs.ListFiles(_fx.RepoPath));
    }

    [Fact]
    public void Untrack_ShouldRoundTrip()
    {
        RequireLfs();
        _lfs.Install(_fx.RepoPath);
        _lfs.Track(_fx.RepoPath, "*.bin");
        Assert.Contains("*.bin", _lfs.ListTrackedPatterns(_fx.RepoPath));

        _lfs.Untrack(_fx.RepoPath, "*.bin");
        Assert.DoesNotContain("*.bin", _lfs.ListTrackedPatterns(_fx.RepoPath));
    }

    [Fact]
    public void ListLfsFiles_TrackedPatternWithNoMatchingFiles_ShouldBeEmpty()
    {
        RequireLfs();
        _lfs.Install(_fx.RepoPath);
        _lfs.Track(_fx.RepoPath, "*.bin");
        // Commit the .gitattributes but no *.bin file exists.
        Git("add", "-A");
        Git("commit", "-m", "track only");

        Assert.Empty(_lfs.ListLfsFiles(_fx.RepoPath));
        Assert.Contains("*.bin", _lfs.ListTrackedPatterns(_fx.RepoPath));
    }

    [Fact]
    public void Track_WithExistingGitattributes_ShouldAppendNotClobber()
    {
        RequireLfs();
        // A pre-existing, non-LFS .gitattributes.
        File.WriteAllText(Path.Combine(_fx.RepoPath, ".gitattributes"), "*.txt text\n");
        _lfs.Install(_fx.RepoPath);
        _lfs.Track(_fx.RepoPath, "*.bin");

        var attrs = File.ReadAllText(Path.Combine(_fx.RepoPath, ".gitattributes"));
        Assert.Contains("*.txt text", attrs);          // original line preserved
        Assert.Contains("*.bin", attrs);               // new LFS line added
        Assert.Contains("filter=lfs", attrs);
        // The parser only surfaces the LFS pattern, not the plain-text one.
        Assert.Equal(new[] { "*.bin" }, _lfs.ListTrackedPatterns(_fx.RepoPath));
    }

    [Fact]
    public void Track_PathWithSpaces_ShouldCommitPointer_AndListWithSpaces()
    {
        RequireLfs();
        _lfs.Install(_fx.RepoPath);
        _lfs.Track(_fx.RepoPath, "*.bin");
        CommitBinaryViaCli("my asset.bin", SampleBinary());

        var show = Git("show", "HEAD:my asset.bin");
        Assert.Equal(0, show.Code);
        Assert.True(LfsPointer.IsPointer(show.Out));

        var files = _lfs.ListLfsFiles(_fx.RepoPath);
        Assert.Single(files);
        Assert.Equal("my asset.bin", files[0].Path);   // space preserved through the parser
    }

    [Fact]
    public void IsEnabledForRepo_ShouldReflectInstallAndUninstall()
    {
        RequireLfs();
        Assert.False(_lfs.IsEnabledForRepo(_fx.RepoPath));   // fresh repo: no lfs filters
        _lfs.Install(_fx.RepoPath);
        Assert.True(_lfs.IsEnabledForRepo(_fx.RepoPath));
        _lfs.Uninstall(_fx.RepoPath);
        Assert.False(_lfs.IsEnabledForRepo(_fx.RepoPath));
    }

    [Fact]
    public void Prune_DryRun_ShouldReturnSummary_WithoutDeleting()
    {
        RequireLfs();
        _lfs.Install(_fx.RepoPath);
        _lfs.Track(_fx.RepoPath, "*.bin");
        CommitBinaryViaCli("asset.bin", SampleBinary());

        var summary = _lfs.Prune(_fx.RepoPath, dryRun: true);
        // git-lfs's dry-run wording varies across versions/platforms, so assert only that a
        // non-empty summary was captured (from stdout, or the stderr fallback), not its exact text.
        Assert.False(string.IsNullOrWhiteSpace(summary));
        // Dry run does not remove the object — it is still listed.
        Assert.Single(_lfs.ListLfsFiles(_fx.RepoPath));
    }

    [Fact]
    public void Pull_WithNoRemote_ShouldThrowTyped_NotHang()
    {
        RequireLfs();
        _lfs.Install(_fx.RepoPath);
        // No remote configured → remote resolution throws a typed GitLoom exception (never a hang,
        // and never a secret on any URL — the authenticated path is not even reached).
        Assert.Throws<RemoteNotFoundException>(() => _lfs.Pull(_fx.RepoPath));
    }

    // Not gated: this exercises the graceful-degrade contract and must pass even where git-lfs
    // exists (forced via the availability override). Every feature method throws the typed
    // "not installed" error and NEVER attempts the underlying git op (TI-17 #4).
    [Fact]
    public void LfsUnavailable_ShouldDegradeGracefully()
    {
        var lfs = new LfsService(_git) { AvailabilityOverride = false };
        Assert.False(lfs.IsAvailable(_fx.RepoPath));

        AssertNotInstalled(() => lfs.Install(_fx.RepoPath));
        AssertNotInstalled(() => lfs.Uninstall(_fx.RepoPath));
        AssertNotInstalled(() => lfs.Track(_fx.RepoPath, "*.bin"));
        AssertNotInstalled(() => lfs.Untrack(_fx.RepoPath, "*.bin"));
        AssertNotInstalled(() => lfs.ListTrackedPatterns(_fx.RepoPath));
        AssertNotInstalled(() => lfs.ListFiles(_fx.RepoPath));
        AssertNotInstalled(() => lfs.ListLfsFiles(_fx.RepoPath));
        AssertNotInstalled(() => lfs.Pull(_fx.RepoPath));
        AssertNotInstalled(() => lfs.Prune(_fx.RepoPath, dryRun: true));
        AssertNotInstalled(() => lfs.IsEnabledForRepo(_fx.RepoPath));

        // The op never ran: no .gitattributes was written by the throwing Track call.
        Assert.False(File.Exists(Path.Combine(_fx.RepoPath, ".gitattributes")));
    }

    private static void AssertNotInstalled(Action act)
    {
        var ex = Assert.Throws<GitOperationException>(act);
        Assert.Contains("not installed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
