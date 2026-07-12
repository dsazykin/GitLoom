using System.Collections.Generic;
using System.Linq;
using GitLoom.Core.Safety;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-30 — the pure scan engine. Pins the exact finding set (kinds/severities/paths/lines + ordering)
/// for a mixed input, and asserts the CRITICAL invariant: no produced message ever contains the
/// planted secret value. No IO, no git.
/// </summary>
public class PreCommitScanEngineTests
{
    private const string PlantedSecret = "s3cr3tV4lu3_x9Q2wZ";

    private static IReadOnlyList<PreCommitFinding> ScanMixed()
    {
        var entries = new List<(string, string, bool, long)>
        {
            // A hard-coded secret on line 2.
            ("src/config.py", $"import os\npassword = \"{PlantedSecret}\"\nprint(os)\n", false, 120),
            // A clean file — no findings.
            ("README.md", "# Project\n\nNothing to see here.\n", false, 60),
            // Merge markers on lines 1, 3, 5.
            ("merge.txt", "<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> feature\n", false, 200),
            // An oversized binary — flagged by size only, never scanned as text.
            ("assets/blob.bin", "", true, 6L * 1024 * 1024),
        };
        return PreCommitScanEngine.Scan(entries);
    }

    [Fact]
    public void Scan_ShouldProduceThePinnedFindingSet_InDeterministicOrder()
    {
        var findings = ScanMixed();

        Assert.Equal(5, findings.Count);

        // Blockers first, ordered by path (ordinal) then line: merge.txt < src/config.py.
        Assert.Equal((FindingKind.MergeMarker, FindingSeverity.Blocker, "merge.txt", 1, "merge-marker"),
            Tuple(findings[0]));
        Assert.Equal((FindingKind.MergeMarker, FindingSeverity.Blocker, "merge.txt", 3, "merge-marker"),
            Tuple(findings[1]));
        Assert.Equal((FindingKind.MergeMarker, FindingSeverity.Blocker, "merge.txt", 5, "merge-marker"),
            Tuple(findings[2]));
        Assert.Equal((FindingKind.Secret, FindingSeverity.Blocker, "src/config.py", 2, "generic-secret-assignment"),
            Tuple(findings[3]));

        // Then the warning: the oversized binary.
        Assert.Equal(FindingKind.LargeFile, findings[4].Kind);
        Assert.Equal(FindingSeverity.Warning, findings[4].Severity);
        Assert.Equal("assets/blob.bin", findings[4].Path);
        Assert.Equal("large-file", findings[4].Rule);
    }

    [Fact]
    public void Scan_ShouldNeverEchoTheSecretValue_InAnyMessage()
    {
        var findings = ScanMixed();

        Assert.All(findings, f => Assert.DoesNotContain(PlantedSecret, f.Message));
        // Belt-and-suspenders: the secret is nowhere in any field of any finding.
        Assert.All(findings, f =>
        {
            Assert.DoesNotContain(PlantedSecret, f.Message);
            Assert.DoesNotContain(PlantedSecret, f.Rule);
            Assert.DoesNotContain(PlantedSecret, f.Path);
        });
    }

    [Fact]
    public void Scan_ShouldNotScanBinaryAsText_EvenWhenContentIsPresent()
    {
        // A binary blob whose bytes happen to contain a marker/secret string must not be text-scanned.
        var entries = new List<(string, string, bool, long)>
        {
            ("image.png", "<<<<<<< HEAD\npassword = \"" + PlantedSecret + "\"\n", true /* isBinary */, 4096),
        };
        var findings = PreCommitScanEngine.Scan(entries);
        Assert.Empty(findings);
    }

    [Fact]
    public void Scan_ShouldFlagLargeFile_ByThreshold()
    {
        var opts = new PreCommitScanOptions { MaxFileBytes = 1024 };
        var entries = new List<(string, string, bool, long)>
        {
            ("small.txt", "ok\n", false, 512),
            ("big.txt", "", false, 2048),
        };
        var findings = PreCommitScanEngine.Scan(entries, opts);
        var large = Assert.Single(findings);
        Assert.Equal(FindingKind.LargeFile, large.Kind);
        Assert.Equal("big.txt", large.Path);
    }

    [Fact]
    public void Scan_ShouldRaiseManyFiles_OverThreshold()
    {
        var opts = new PreCommitScanOptions { ManyFilesThreshold = 3 };
        var entries = Enumerable.Range(0, 4)
            .Select(i => ($"f{i}.txt", "clean\n", false, 10L))
            .ToList<(string, string, bool, long)>();

        var findings = PreCommitScanEngine.Scan(entries, opts);
        var many = Assert.Single(findings, f => f.Kind == FindingKind.ManyFiles);
        Assert.Equal(FindingSeverity.Warning, many.Severity);
        Assert.Equal("", many.Path);
    }

    [Fact]
    public void Scan_ShouldNotMatchMergeMarker_MidLine()
    {
        // Markers only at line start; a diff doc that mentions them mid-line must not fire.
        var entries = new List<(string, string, bool, long)>
        {
            ("docs.md", "Conflicts look like <<<<<<< HEAD in the file.\nEnd with >>>>>>> branch.\n", false, 80),
        };
        Assert.Empty(PreCommitScanEngine.Scan(entries));
    }

    [Fact]
    public void Scan_ShouldReturnNothing_ForACleanTree()
    {
        var entries = new List<(string, string, bool, long)>
        {
            ("a.txt", "hello\nworld\n", false, 12),
            ("b.cs", "public class A { }\n", false, 20),
        };
        Assert.Empty(PreCommitScanEngine.Scan(entries));
    }

    private static (FindingKind, FindingSeverity, string, int?, string) Tuple(PreCommitFinding f)
        => (f.Kind, f.Severity, f.Path, f.Line, f.Rule);
}
