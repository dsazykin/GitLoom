using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-17 #3 (pure): LFS pointer detection. IsPointer is true iff the FIRST line is exactly the LFS
// v1 spec version line; malformed/partial variants are false. Size/OID parsing feeds the diff summary.
public class LfsPointerTests
{
    private const string RealPointer =
        "version https://git-lfs.github.com/spec/v1\n" +
        "oid sha256:394e150401779536293e71470142d31b9af32750fb50c9c548d63632cf512d40\n" +
        "size 21\n";

    [Fact]
    public void IsPointer_RealPointer_ShouldBeTrue()
    {
        Assert.True(LfsPointer.IsPointer(RealPointer));
    }

    [Fact]
    public void IsPointer_VersionLineOnly_NoTrailingNewline_ShouldBeTrue()
    {
        Assert.True(LfsPointer.IsPointer("version https://git-lfs.github.com/spec/v1"));
    }

    [Fact]
    public void IsPointer_CrlfEncoded_ShouldBeTrue()
    {
        Assert.True(LfsPointer.IsPointer("version https://git-lfs.github.com/spec/v1\r\noid sha256:abc\r\nsize 3\r\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a pointer at all")]
    [InlineData("\nversion https://git-lfs.github.com/spec/v1")]                 // leading blank line
    [InlineData("  version https://git-lfs.github.com/spec/v1")]                 // leading spaces
    [InlineData("version https://git-lfs.github.com/spec/v2\noid sha256:x")]     // wrong version
    [InlineData("version https://git-lfs")]                                       // truncated
    [InlineData("oid sha256:abc\nversion https://git-lfs.github.com/spec/v1")]   // version not first
    public void IsPointer_MalformedVariants_ShouldBeFalse(string? content)
    {
        Assert.False(LfsPointer.IsPointer(content));
    }

    [Fact]
    public void ParseSize_ShouldReadSizeField()
    {
        Assert.Equal(21, LfsPointer.ParseSize(RealPointer));
    }

    [Fact]
    public void ParseSize_NonPointer_ShouldBeNull()
    {
        Assert.Null(LfsPointer.ParseSize("hello world"));
    }

    [Fact]
    public void ParseOid_ShouldReadOidField()
    {
        Assert.Equal("394e150401779536293e71470142d31b9af32750fb50c9c548d63632cf512d40",
            LfsPointer.ParseOid(RealPointer));
    }
}
