using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-06: PatchParser is pure and Serialize round-trips Parse byte-identically for LF input.
// The corpus under TestData/patches/ is real git-produced output (LF-locked via .gitattributes).
public class PatchParserTests
{
    public static IEnumerable<object[]> CorpusFiles()
    {
        foreach (var path in Directory.GetFiles(CorpusDir(), "*.patch").OrderBy(p => p))
            yield return new object[] { Path.GetFileName(path) };
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Parse_Serialize_ShouldRoundTripByteIdentically(string fileName)
    {
        var original = ReadCorpus(fileName);

        var reassembled = string.Concat(PatchParser.Parse(original).Select(PatchParser.Serialize));

        Assert.Equal(original, reassembled);
    }

    [Fact]
    public void Parse_ShouldExposeHunkHeaderNumbers_AndSectionHeading()
    {
        // 06 is `git diff -U0`: two hunks with omitted ,1 counts and a section heading
        // (the preceding context line) after the second @@.
        var file = Assert.Single(PatchParser.Parse(ReadCorpus("06-adjacent-hunks.patch")));
        Assert.Equal(2, file.Hunks.Count);

        var h0 = file.Hunks[0];
        Assert.Equal(2, h0.OldStart);
        Assert.Equal(1, h0.OldCount);
        Assert.True(h0.OldCountOmitted);
        Assert.Equal(2, h0.NewStart);
        Assert.True(h0.NewCountOmitted);
        Assert.Equal(" c1", h0.SectionHeading);

        var h1 = file.Hunks[1];
        Assert.Equal(4, h1.OldStart);
        Assert.Equal(4, h1.NewStart);
        Assert.Equal(" c3", h1.SectionHeading);
    }

    [Fact]
    public void Parse_ShouldAttachNoNewlineMarker_ToPrecedingLine()
    {
        var file = Assert.Single(PatchParser.Parse(ReadCorpus("04-no-newline-eof.patch")));
        var lines = file.Hunks[0].Lines;

        var flagged = Assert.Single(lines, l => l.NoNewlineAtEof);
        Assert.Equal("three", flagged.Text);
        // The marker is not itself a DiffLine — it rides on the last content line.
        Assert.Same(lines[^1], flagged);
    }

    [Fact]
    public void Parse_ShouldReturnOneFilePatchPerFile_OnMultiFileDiff()
    {
        var files = PatchParser.Parse(ReadCorpus("03-multi-file.patch"));
        Assert.Equal(2, files.Count);
    }

    private static string ReadCorpus(string fileName) => File.ReadAllText(Path.Combine(CorpusDir(), fileName));

    private static string CorpusDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return Path.Combine(dir ?? AppContext.BaseDirectory, "GitLoom.Tests", "TestData", "patches");
    }
}
