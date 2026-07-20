using System;
using System.Collections.Generic;
using System.IO;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-05 #1-#3 / plan §6 #1-#2: the pure INI merger is the correctness heart of P2-05. Every case
// is fixture-tested against a committed expected file so a byte-level regression (lost user key,
// clobbered comment, mangled CRLF) fails the build.
public class WslConfigMergerTests
{
    // The keys GitLoom wants under [wsl2]. Fixed here so the expected fixtures are stable.
    private static IReadOnlyDictionary<string, string> OurKeys() => new Dictionary<string, string>
    {
        ["memory"] = "6GB",
        ["autoMemoryReclaim"] = "gradual",
    };

    public static IEnumerable<object[]> FixtureCases() => new[]
    {
        new object[] { "empty" },          // brand-new file
        new object[] { "no-wsl2" },        // file with other sections, no [wsl2]
        new object[] { "existing-wsl2" },  // [wsl2] present with an unrelated key
        new object[] { "keys-set" },       // our keys already set — user value must win (no change)
        new object[] { "comments" },       // comments (# and ;) + a trailing unknown section
        new object[] { "crlf" },           // CRLF newlines preserved
    };

    // §6 #1 — Merge output matches the committed expected file, byte-for-byte.
    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void WslConfigMerger_Fixtures(string caseName)
    {
        var input = ReadFixture($"{caseName}.input.wslconfig");
        var expected = ReadFixture($"{caseName}.expected.wslconfig");

        var merged = WslConfigMerger.Merge(input, OurKeys());

        Assert.Equal(expected, merged);
    }

    // §6 #1 — a null (missing file) behaves like an empty file.
    [Fact]
    public void Merge_NullContent_CreatesSectionLikeEmptyFile()
    {
        var fromNull = WslConfigMerger.Merge(null, OurKeys());
        var fromEmpty = WslConfigMerger.Merge(string.Empty, OurKeys());
        Assert.Equal(fromEmpty, fromNull);
        Assert.Contains("[wsl2]", fromNull, StringComparison.Ordinal);
    }

    // Edge row 1 — an existing user value wins; ours is never written over it.
    [Fact]
    public void Merge_ShouldPreserveExistingUserKeys_AndAddOnlyOurs()
    {
        var input = ReadFixture("keys-set.input.wslconfig");
        var merged = WslConfigMerger.Merge(input, OurKeys());

        Assert.Equal(input, merged);                              // no change at all
        Assert.Contains("memory = 12GB", merged, StringComparison.Ordinal);   // user value kept
        Assert.DoesNotContain("6GB", merged, StringComparison.Ordinal);       // ours NOT applied
        Assert.Contains("dropcache", merged, StringComparison.Ordinal);
    }

    // §6 #2 — merging twice equals merging once (idempotent) for every fixture.
    [Theory]
    [MemberData(nameof(FixtureCases))]
    public void Merge_ShouldBeIdempotent(string caseName)
    {
        var input = ReadFixture($"{caseName}.input.wslconfig");

        var once = WslConfigMerger.Merge(input, OurKeys());
        var twice = WslConfigMerger.Merge(once, OurKeys());

        Assert.Equal(once, twice);
    }

    // §6 #2 — the merger is pure: no instance state / IO surface, and deterministic across calls.
    [Fact]
    public void Merger_IsPure_NoIO()
    {
        var type = typeof(WslConfigMerger);
        Assert.True(type.IsAbstract && type.IsSealed, "WslConfigMerger must be a static class.");
        Assert.Empty(type.GetFields(System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Static
            | System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.NonPublic));

        var input = ReadFixture("comments.input.wslconfig");
        var a = WslConfigMerger.Merge(input, OurKeys());
        var b = WslConfigMerger.Merge(input, OurKeys());
        Assert.Equal(a, b);
    }

    // TI-P2-05 #3 — memory default = min(50% RAM, 8GB), floored to whole GB (min 1GB).
    [Theory]
    [InlineData(1L, "1GB")]                               // <1GB RAM floors to the 1GB minimum
    [InlineData(2L, "1GB")]                               // 2GB RAM → half = 1GB
    [InlineData(4L, "2GB")]
    [InlineData(8L, "4GB")]
    [InlineData(16L, "8GB")]                              // half = 8GB = cap
    [InlineData(32L, "8GB")]                              // half = 16GB → capped at 8GB
    [InlineData(64L, "8GB")]
    public void Merge_MemoryDefault_ShouldBeMinHalfRamOr8Gb(long ramGb, string expected)
    {
        var bytes = ramGb * 1024L * 1024 * 1024;
        Assert.Equal(expected, WslConfigMergeStep.ComputeMemoryValue(bytes));
    }

    // ---- Audit fix #12: uninstall reverts GitLoom's [wsl2] keys (conservatively) -------------------

    [Fact]
    public void Remove_MergeThenRemove_RestoresTheOriginalFileByteForByte()
    {
        var original = "# user notes\n[experimental]\nsparseVhd=true\n";
        var merged = WslConfigMerger.Merge(original, OurKeys());

        Assert.Equal(original, WslConfigMerger.RemoveGitLoomKeys(merged));
    }

    [Fact]
    public void Remove_FreshFileCreatedByMerge_BecomesEmpty()
    {
        var merged = WslConfigMerger.Merge(null, OurKeys());

        Assert.Equal(string.Empty, WslConfigMerger.RemoveGitLoomKeys(merged));
    }

    [Fact]
    public void Remove_UserTunedValues_Survive()
    {
        // The user edited memory after install (not our <N>GB shape) and set their own reclaim mode:
        // neither is ours to delete.
        var content = "[wsl2]\nmemory=12000MB\nautoMemoryReclaim=dropcache\nprocessors=4\n";

        Assert.Equal(content, WslConfigMerger.RemoveGitLoomKeys(content));
    }

    [Fact]
    public void Remove_OurKeysAmongUserKeys_RemovesOnlyOurs_AndKeepsTheSection()
    {
        var content = "[wsl2]\nprocessors=4\nmemory=8GB\nautoMemoryReclaim=gradual\n\n[experimental]\nsparseVhd=true\n";

        var reverted = WslConfigMerger.RemoveGitLoomKeys(content);

        Assert.Equal("[wsl2]\nprocessors=4\n\n[experimental]\nsparseVhd=true\n", reverted);
    }

    [Fact]
    public void Remove_IsIdempotent_AndNoOpWithoutWsl2Section()
    {
        var noSection = "[experimental]\nsparseVhd=true\n";
        Assert.Equal(noSection, WslConfigMerger.RemoveGitLoomKeys(noSection));

        var merged = WslConfigMerger.Merge("[network]\ngenerateHosts=false\n", OurKeys());
        var once = WslConfigMerger.RemoveGitLoomKeys(merged);
        Assert.Equal(once, WslConfigMerger.RemoveGitLoomKeys(once));
    }

    [Fact]
    public void Remove_PreservesCrlfNewlines()
    {
        var content = "[wsl2]\r\nprocessors=2\r\nmemory=6GB\r\n";

        Assert.Equal("[wsl2]\r\nprocessors=2\r\n", WslConfigMerger.RemoveGitLoomKeys(content));
    }

    private static string ReadFixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        // ReadAllText preserves the file's exact newline bytes — essential for the CRLF fixture.
        return File.ReadAllText(Path.Combine(dir ?? AppContext.BaseDirectory,
            "Mainguard.Tests", "Fixtures", "WslConfig", name));
    }
}
