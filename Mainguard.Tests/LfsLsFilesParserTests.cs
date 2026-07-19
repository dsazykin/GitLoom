using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Xunit;

namespace Mainguard.Tests;

// TI-17 (pure): git lfs ls-files parser. Format "<oid> <*|-> <path>" — status char is content-present
// (*) vs pointer-only (-); the path is the remainder verbatim (spaces preserved). Pinned to real
// git-lfs 3.5.1 output captured from a local fixture.
public class LfsLsFilesParserTests
{
    [Fact]
    public void Parse_ShortOids_ShouldSplitOidStatusAndPath()
    {
        var output =
            "394e150401 * my file.bin\n" +   // path with a space, content present
            "f4bae3678f - other.bin\n";      // pointer only (not downloaded)

        var files = LfsLsFilesParser.Parse(output);

        Assert.Equal(2, files.Count);
        Assert.Equal("394e150401", files[0].Oid);
        Assert.Equal("my file.bin", files[0].Path);
        Assert.True(files[0].IsDownloaded);
        Assert.Equal("other.bin", files[1].Path);
        Assert.False(files[1].IsDownloaded);
    }

    [Fact]
    public void Parse_LongOids_ShouldKeepFullOid()
    {
        var output = "394e150401779536293e71470142d31b9af32750fb50c9c548d63632cf512d40 * my file.bin\n";

        var files = LfsLsFilesParser.Parse(output);

        Assert.Single(files);
        Assert.Equal(64, files[0].Oid.Length);
        Assert.Equal("my file.bin", files[0].Path);
    }

    [Fact]
    public void Parse_CrlfAndBlankLines_ShouldBeTolerated()
    {
        var output = "\r\n394e150401 * a.bin\r\n\r\n";

        var files = LfsLsFilesParser.Parse(output);

        Assert.Single(files);
        Assert.Equal("a.bin", files[0].Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage without structure")]
    public void Parse_EmptyOrJunk_ShouldReturnEmpty(string? output)
    {
        Assert.Empty(LfsLsFilesParser.Parse(output));
    }
}
