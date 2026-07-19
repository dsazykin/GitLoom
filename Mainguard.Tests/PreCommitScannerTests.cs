using System;
using System.Linq;
using Mainguard.Agents;
using Mainguard.Agents.Services;
using Mainguard.Git.Safety;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-30 — the <see cref="PreCommitScanner"/> over a real local fixture repo: it reads the STAGED
/// tree via ExecuteWithRepo, runs the pure engine, and reports secrets / merge markers / large files.
/// A clean stage produces nothing. Also re-asserts the never-leak invariant end-to-end. All
/// LibGit2Sharp-driven; tagged RequiresGitCli per the T-30 contract.
/// </summary>
[Trait("Category", "RequiresGitCli")]
public sealed class PreCommitScannerTests : IDisposable
{
    private const string PlantedAwsKey = "AKIAIOSFODNN7EXAMPLE";

    private readonly TempRepoFixture _fx = new();
    private readonly GitService _git = new();
    private readonly PreCommitScanner _scanner;

    public PreCommitScannerTests() => _scanner = new PreCommitScanner(_git);

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void ScanStaged_ShouldReportSecretMergeMarkerAndLargeFile()
    {
        _fx.WriteFile("creds.env", $"AWS_ACCESS_KEY_ID={PlantedAwsKey}\n");
        _fx.WriteFile("conflict.txt", "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> feat\n");
        _fx.WriteFile("blob.bin", new string('A', (int)(6L * 1024 * 1024))); // > 5 MB
        _git.StageFile(_fx.RepoPath, "creds.env");
        _git.StageFile(_fx.RepoPath, "conflict.txt");
        _git.StageFile(_fx.RepoPath, "blob.bin");

        var findings = _scanner.ScanStaged(_fx.RepoPath);

        Assert.Contains(findings, f => f.Kind == FindingKind.Secret
            && f.Rule == "aws-access-key-id" && f.Path == "creds.env");
        Assert.Contains(findings, f => f.Kind == FindingKind.MergeMarker && f.Path == "conflict.txt");
        Assert.Contains(findings, f => f.Kind == FindingKind.LargeFile && f.Path == "blob.bin");

        // Never-leak, end-to-end: the AWS key value appears in no finding message.
        Assert.All(findings, f => Assert.DoesNotContain(PlantedAwsKey, f.Message));
    }

    [Fact]
    public void ScanStaged_ShouldReturnNothing_ForACleanStage()
    {
        _fx.WriteFile("hello.txt", "just some ordinary text\nnothing secret here\n");
        _git.StageFile(_fx.RepoPath, "hello.txt");

        Assert.Empty(_scanner.ScanStaged(_fx.RepoPath));
    }

    [Fact]
    public void ScanStaged_ShouldOnlyScanStagedChanges_NotTheWorkingTree()
    {
        // A secret written but NOT staged must not be reported (we scan the index only).
        _fx.WriteFile("staged.txt", "clean\n");
        _git.StageFile(_fx.RepoPath, "staged.txt");
        _fx.WriteFile("unstaged.env", $"KEY={PlantedAwsKey}\n"); // written, never staged

        Assert.Empty(_scanner.ScanStaged(_fx.RepoPath));
    }

    [Fact]
    public void ScanStaged_ShouldRespectCustomSizeThreshold()
    {
        _fx.WriteFile("mid.bin", new string('A', 200_000)); // ~200 KB
        _git.StageFile(_fx.RepoPath, "mid.bin");

        // Default 5 MB → no LargeFile; a 100 KB cap → flagged.
        Assert.DoesNotContain(_scanner.ScanStaged(_fx.RepoPath), f => f.Kind == FindingKind.LargeFile);
        var tight = _scanner.ScanStaged(_fx.RepoPath, new PreCommitScanOptions { MaxFileBytes = 100_000 });
        Assert.Contains(tight, f => f.Kind == FindingKind.LargeFile && f.Path == "mid.bin");
    }
}
