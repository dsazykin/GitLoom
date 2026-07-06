using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-13 (pure): trailing-whitespace marker detection. Pinned exact ranges.
public class WhitespaceMarkersTests
{
    [Fact]
    public void TrailingWhitespace_EmptyLine_ShouldBeNull()
        => Assert.Null(WhitespaceMarkers.TrailingWhitespace(""));

    [Fact]
    public void TrailingWhitespace_NoTrailing_ShouldBeNull()
        => Assert.Null(WhitespaceMarkers.TrailingWhitespace("code();"));

    [Fact]
    public void TrailingWhitespace_TrailingSpaces_ShouldSpanOnlyTheTrailingRun()
    {
        var span = WhitespaceMarkers.TrailingWhitespace("code();   ");
        Assert.Equal((7, 3), span);
    }

    [Fact]
    public void TrailingWhitespace_TrailingTab_ShouldSpanTheTab()
    {
        var span = WhitespaceMarkers.TrailingWhitespace("x\t");
        Assert.Equal((1, 1), span);
    }

    [Fact]
    public void TrailingWhitespace_AllWhitespaceLine_ShouldSpanWholeLine()
    {
        var span = WhitespaceMarkers.TrailingWhitespace("   ");
        Assert.Equal((0, 3), span);
    }

    [Fact]
    public void TrailingWhitespace_LeadingIndentOnly_ShouldBeNull()
    {
        // Leading indentation is not trailing whitespace.
        Assert.Null(WhitespaceMarkers.TrailingWhitespace("    code"));
    }
}
