using GitLoom.Core.Security;

namespace GitLoom.Tests;

/// <summary>TI-P2-22 #3: the <c>gitloom://</c> parse matrix and the no-secret code-path guarantee.</summary>
public class DeepLinkTests
{
    [Fact]
    public void Parse_ValidVerbs_ShouldProduceTypedCommands()
    {
        Assert.Equal(new DeepLinkCommand.OpenRepo("abc123"),
            DeepLinkParser.Parse("gitloom://open-repo/abc123").Command);

        Assert.Equal(new DeepLinkCommand.OpenPr("github.com", "octo/repo", 42),
            DeepLinkParser.Parse("gitloom://open-pr/github.com/octo/repo/42").Command);

        Assert.Equal(new DeepLinkCommand.OpenAgent("agent-7"),
            DeepLinkParser.Parse("gitloom://open-agent/agent-7").Command);
    }

    [Theory]
    [InlineData("gitloom://open-repo/x?token=abcdef")]
    [InlineData("gitloom://open-pr/github.com/o/r/1?code=xyz")]
    [InlineData("gitloom://open-agent/a?access_token=zzz")]
    [InlineData("gitloom://open-repo/x#secret=shh")]
    [InlineData("gitloom://open-agent/a?client_secret=nope")]
    public void Parse_SecretShapedLinks_ShouldBeRejected(string uri)
    {
        Assert.Equal(DeepLinkOutcome.Rejected, DeepLinkParser.Parse(uri).Outcome);
    }

    [Theory]
    [InlineData("gitloom://frobnicate/xyz")]
    [InlineData("gitloom://open-widget/1")]
    public void Parse_UnknownVerbs_ShouldBeIgnoredGracefully(string uri)
    {
        Assert.Equal(DeepLinkOutcome.Ignored, DeepLinkParser.Parse(uri).Outcome);
    }

    [Theory]
    [InlineData("https://example.com/x")]        // wrong scheme
    [InlineData("gitloom://open-pr/only/two")]   // malformed pr (no number)
    [InlineData("gitloom://open-repo/a/b")]      // repo expects one segment
    [InlineData("not a uri")]
    public void Parse_MalformedOrWrongScheme_ShouldBeRejected(string uri)
    {
        Assert.Equal(DeepLinkOutcome.Rejected, DeepLinkParser.Parse(uri).Outcome);
    }

    [Fact]
    public void Builder_ShouldRoundTrip_AndNeverEmitSecrets()
    {
        var repoLink = DeepLinkBuilder.OpenRepo("hash-9");
        Assert.Equal(new DeepLinkCommand.OpenRepo("hash-9"), DeepLinkParser.Parse(repoLink).Command);

        var prLink = DeepLinkBuilder.OpenPr("gitlab.com", "group/sub/proj", 7);
        Assert.Equal(new DeepLinkCommand.OpenPr("gitlab.com", "group/sub/proj", 7), DeepLinkParser.Parse(prLink).Command);

        // The builder refuses to place a secret-shaped identifier in a link (code-path guarantee).
        Assert.Throws<System.ArgumentException>(() => DeepLinkBuilder.OpenRepo("access_token"));
    }
}
