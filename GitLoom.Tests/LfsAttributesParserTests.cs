using GitLoom.Core.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

// TI-17 (pure): .gitattributes → LFS-tracked patterns. A pattern is LFS-tracked when its line carries
// filter=lfs; the pattern is the first token. Comments/blank lines skipped; [[:space:]] decodes to space.
public class LfsAttributesParserTests
{
    [Fact]
    public void Parse_RealGitattributes_ShouldReturnPattern()
    {
        // Exactly what `git lfs track "*.bin"` writes.
        var patterns = LfsAttributesParser.Parse("*.bin filter=lfs diff=lfs merge=lfs -text\n");

        Assert.Single(patterns);
        Assert.Equal("*.bin", patterns[0]);
    }

    [Fact]
    public void Parse_ShouldIgnoreNonLfsAndCommentLines()
    {
        var content =
            "# a comment\n" +
            "*.txt text\n" +                                   // not an LFS line
            "*.psd filter=lfs diff=lfs merge=lfs -text\n" +
            "\n" +
            "*.zip filter=lfs diff=lfs merge=lfs -text\n";

        var patterns = LfsAttributesParser.Parse(content);

        Assert.Equal(new[] { "*.psd", "*.zip" }, patterns);
    }

    [Fact]
    public void Parse_SpaceEncodedPattern_ShouldDecodeToSpace()
    {
        var patterns = LfsAttributesParser.Parse("my[[:space:]]dir/*.bin filter=lfs diff=lfs merge=lfs -text\n");

        Assert.Single(patterns);
        Assert.Equal("my dir/*.bin", patterns[0]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Parse_EmptyContent_ShouldReturnEmpty(string? content)
    {
        Assert.Empty(LfsAttributesParser.Parse(content));
    }
}
