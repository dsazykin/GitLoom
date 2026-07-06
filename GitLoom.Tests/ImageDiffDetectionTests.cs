using System.Text;
using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-13 (pure): image-candidate detection table + binary sniffing + size summary formatting.
public class ImageDiffDetectionTests
{
    [Theory]
    // path, isBinary -> expected
    [InlineData("logo.png", true, true)]
    [InlineData("photo.JPG", true, true)]        // case-insensitive extension
    [InlineData("scan.jpeg", true, true)]
    [InlineData("icon.ico", true, true)]
    [InlineData("art.webp", true, true)]
    [InlineData("anim.gif", true, true)]
    [InlineData("pic.bmp", true, true)]
    [InlineData("logo.png", false, false)]       // image extension but not binary (e.g. text/SVG-ish)
    [InlineData("archive.zip", true, false)]     // binary but not an image
    [InlineData("notes.txt", true, false)]
    [InlineData("README", true, false)]          // no extension
    [InlineData("", true, false)]
    [InlineData(null, true, false)]
    public void IsImageCandidate_ByExtensionAndBinaryFlag(string? path, bool isBinary, bool expected)
        => Assert.Equal(expected, ImageDiffDetection.IsImageCandidate(path, isBinary));

    [Theory]
    [InlineData("diff --git a/x b/x\nGIT binary patch\n...", true)]
    [InlineData("diff --git a/x b/x\nBinary files a/x and b/x differ\n", true)]
    [InlineData("@@ -1 +1 @@\n-a\n+b\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void DiffIndicatesBinary_ShouldRecognizeGitBinaryMarkers(string? diff, bool expected)
        => Assert.Equal(expected, ImageDiffDetection.DiffIndicatesBinary(diff));

    [Fact]
    public void LooksBinary_WithNulByte_ShouldBeTrue()
        => Assert.True(ImageDiffDetection.LooksBinary(new byte[] { 0x89, 0x50, 0x00, 0x4E }));

    [Fact]
    public void LooksBinary_PlainText_ShouldBeFalse()
        => Assert.False(ImageDiffDetection.LooksBinary(Encoding.UTF8.GetBytes("hello world")));

    [Fact]
    public void LooksBinary_EmptySample_ShouldBeFalse()
        => Assert.False(ImageDiffDetection.LooksBinary(System.ReadOnlySpan<byte>.Empty));

    [Theory]
    [InlineData(0, 0, "Binary file changed (0 B → 0 B)")]
    [InlineData(1024, 2048, "Binary file changed (1 KB → 2 KB)")]
    [InlineData(1536, 512, "Binary file changed (1.5 KB → 512 B)")]
    public void FormatBinarySummary_ShouldRenderHumanSizes(long oldSize, long newSize, string expected)
        => Assert.Equal(expected, ImageDiffDetection.FormatBinarySummary(oldSize, newSize));
}
