using System.Linq;
using Mainguard.Git.Commits;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-32 (parser) — pins the pure <see cref="IssueReferenceParser"/> across every case the blame → PR
/// feature relies on: a bare <c>#12</c> (attributed to the PR's own repo), a cross-repo <c>owner/repo#7</c>,
/// closing keywords (<c>closes/fixes/resolves</c>), multiple refs in one line, no-match text, and dedup of a
/// closing-keyword mention against a plain mention of the same issue. Pure — no IO/host/git.
/// </summary>
public class IssueReferenceParserTests
{
    [Fact]
    public void Bare_Reference_UsesDefaultRepo()
    {
        var refs = IssueReferenceParser.Parse("Fixes a thing, see #12.", "octocat/hello-world");
        var one = Assert.Single(refs);
        Assert.Equal(12, one.Number);
        Assert.Equal("octocat/hello-world", one.RepoFullName);
    }

    [Fact]
    public void Bare_Reference_WithNoDefaultRepo_HasEmptyRepo()
    {
        var one = Assert.Single(IssueReferenceParser.Parse("closes #4"));
        Assert.Equal(4, one.Number);
        Assert.Equal("", one.RepoFullName);
    }

    [Fact]
    public void CrossRepo_Reference_KeepsExplicitRepo()
    {
        var one = Assert.Single(IssueReferenceParser.Parse("relates to octocat/spec#7", "octocat/hello-world"));
        Assert.Equal(7, one.Number);
        Assert.Equal("octocat/spec", one.RepoFullName);
    }

    [Theory]
    [InlineData("Closes #3")]
    [InlineData("closed #3")]
    [InlineData("fixes #3")]
    [InlineData("fixed #3")]
    [InlineData("fix #3")]
    [InlineData("resolves #3")]
    [InlineData("resolved #3")]
    [InlineData("resolve #3")]
    public void ClosingKeywords_AreCaptured(string text)
    {
        var one = Assert.Single(IssueReferenceParser.Parse(text, "o/r"));
        Assert.Equal(3, one.Number);
    }

    [Fact]
    public void MultipleReferences_InOneLine_AllCaptured_InOrder()
    {
        var refs = IssueReferenceParser.Parse("fixes #4 and #5", "o/r");
        Assert.Equal(new[] { 4, 5 }, refs.Select(r => r.Number).ToArray());
    }

    [Fact]
    public void ClosingKeyword_And_PlainMention_OfSameIssue_Dedup()
    {
        // "Closes #12" (keyword) and a later plain "#12" collapse to one entry (dedup by repo + number).
        var refs = IssueReferenceParser.Parse("Closes #12.\nSee also #12 for context.", "o/r");
        var one = Assert.Single(refs);
        Assert.Equal(12, one.Number);
    }

    [Fact]
    public void SameNumber_DifferentRepos_AreDistinct()
    {
        var refs = IssueReferenceParser.Parse("#7 and other/repo#7", "octocat/hello-world");
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.RepoFullName == "octocat/hello-world" && r.Number == 7);
        Assert.Contains(refs, r => r.RepoFullName == "other/repo" && r.Number == 7);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no issue references here")]
    [InlineData("# Heading with a space")]
    [InlineData("color is #fff not an issue")]
    [InlineData("identifier abc#7 is not a bare ref")]
    public void NoMatch_ReturnsEmpty(string? text)
        => Assert.Empty(IssueReferenceParser.Parse(text, "o/r"));

    [Fact]
    public void MixedBody_ExtractsAllDistinct()
    {
        const string body = "Implements the feature.\n\nCloses #12 and resolves #12.\nfixes #7 and #8.\nAlso octocat/spec#3.";
        var refs = IssueReferenceParser.Parse(body, "octocat/hello-world");
        Assert.Equal(4, refs.Count); // #12, #7, #8, octocat/spec#3
        Assert.Contains(refs, r => r.Number == 3 && r.RepoFullName == "octocat/spec");
    }
}
