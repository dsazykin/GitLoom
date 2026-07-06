using System.Linq;
using GitLoom.Core.Commits;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-31 — the pure conventional-commit engine. <see cref="ConventionalCommitBuilder.Build"/> output is
/// pinned byte-exact; <see cref="ConventionalCommitBuilder.Validate"/> errors/warnings and the
/// <see cref="ConventionalCommitBuilder.Parse"/> round-trip are asserted. No IO — deterministic.
/// </summary>
public sealed class ConventionalCommitBuilderTests
{
    // ---- Build (pinned exact strings) ----

    [Fact]
    public void Build_FeatWithScope_Body_CoAuthor_Closes_IsPinned()
    {
        var draft = new ConventionalCommitDraft
        {
            Type = "feat",
            Scope = "api",
            Description = "add pagination",
            Body = "Adds cursor-based pagination to the list endpoint.",
            CoAuthors = new[] { "Jane Doe <jane@example.com>" },
            ClosesIssues = new[] { "#42" },
        };

        var expected =
            "feat(api): add pagination\n\n" +
            "Adds cursor-based pagination to the list endpoint.\n\n" +
            "Closes #42\n" +
            "Co-authored-by: Jane Doe <jane@example.com>";

        Assert.Equal(expected, ConventionalCommitBuilder.Build(draft));
    }

    [Fact]
    public void Build_FixNoScope_Breaking_IsPinned()
    {
        var draft = new ConventionalCommitDraft
        {
            Type = "fix",
            Description = "drop legacy tokens",
            Breaking = true,
            BreakingDescription = "token_v1 is no longer accepted",
        };

        var expected =
            "fix!: drop legacy tokens\n\n" +
            "BREAKING CHANGE: token_v1 is no longer accepted";

        Assert.Equal(expected, ConventionalCommitBuilder.Build(draft));
    }

    [Fact]
    public void Build_MinimalNoScopeNoBody_IsJustHeader()
    {
        var draft = new ConventionalCommitDraft { Type = "feat", Description = "add dark mode" };
        Assert.Equal("feat: add dark mode", ConventionalCommitBuilder.Build(draft));
    }

    [Fact]
    public void Build_EmptyScope_HasNoParentheses()
    {
        var draft = new ConventionalCommitDraft { Type = "docs", Scope = "", Description = "fix a typo" };
        Assert.Equal("docs: fix a typo", ConventionalCommitBuilder.Build(draft));
    }

    [Fact]
    public void Build_MultipleCoAuthors_OneTrailerEach_MalformedDropped()
    {
        var draft = new ConventionalCommitDraft
        {
            Type = "chore",
            Description = "bump deps",
            CoAuthors = new[] { "Ann <ann@x.io>", "not an email", "Bo <bo@y.io>" },
        };

        var expected =
            "chore: bump deps\n\n" +
            "Co-authored-by: Ann <ann@x.io>\n" +
            "Co-authored-by: Bo <bo@y.io>";

        Assert.Equal(expected, ConventionalCommitBuilder.Build(draft));
    }

    [Fact]
    public void Build_BreakingWithoutDescription_StillEmitsBangAndFooter()
    {
        var draft = new ConventionalCommitDraft { Type = "refactor", Description = "rework config", Breaking = true };
        Assert.Equal("refactor!: rework config\n\nBREAKING CHANGE:", ConventionalCommitBuilder.Build(draft));
    }

    [Fact]
    public void Build_BareIssueNumber_GetsHashPrefix()
    {
        var draft = new ConventionalCommitDraft
        {
            Type = "fix",
            Description = "handle nulls",
            ClosesIssues = new[] { "7", "org/repo#9" },
        };

        var expected =
            "fix: handle nulls\n\n" +
            "Closes #7\n" +
            "Closes org/repo#9";

        Assert.Equal(expected, ConventionalCommitBuilder.Build(draft));
    }

    // ---- Validate ----

    [Fact]
    public void Validate_UnknownType_IsError()
    {
        var issues = ConventionalCommitBuilder.Validate(new ConventionalCommitDraft { Type = "banana", Description = "do a thing" });
        Assert.Contains(issues, i => i.IsError && i.Field == "Type");
    }

    [Fact]
    public void Validate_KnownType_HasNoTypeError()
    {
        var issues = ConventionalCommitBuilder.Validate(new ConventionalCommitDraft { Type = "feat", Description = "add a thing" });
        Assert.DoesNotContain(issues, i => i.Field == "Type");
    }

    [Fact]
    public void Validate_EmptyDescription_IsError()
    {
        var issues = ConventionalCommitBuilder.Validate(new ConventionalCommitDraft { Type = "feat", Description = "" });
        Assert.Contains(issues, i => i.IsError && i.Field == "Description");
    }

    [Fact]
    public void Validate_SubjectOver72_IsWarning()
    {
        var draft = new ConventionalCommitDraft
        {
            Type = "feat",
            Description = "add a very long subject line that comfortably exceeds the seventy two character soft limit",
        };
        var issues = ConventionalCommitBuilder.Validate(draft);
        Assert.Contains(issues, i => !i.IsError && i.Message.Contains("Subject line"));
        Assert.DoesNotContain(issues, i => i.IsError);
    }

    [Fact]
    public void Validate_MalformedCoAuthor_IsError()
    {
        var draft = new ConventionalCommitDraft
        {
            Type = "feat",
            Description = "add a thing",
            CoAuthors = new[] { "Nobody Here" },
        };
        var issues = ConventionalCommitBuilder.Validate(draft);
        Assert.Contains(issues, i => i.IsError && i.Field == "CoAuthors");
    }

    [Fact]
    public void Validate_WellFormedCoAuthor_HasNoCoAuthorError()
    {
        var draft = new ConventionalCommitDraft
        {
            Type = "feat",
            Description = "add a thing",
            CoAuthors = new[] { "Jane Doe <jane@example.com>" },
        };
        Assert.DoesNotContain(ConventionalCommitBuilder.Validate(draft), i => i.Field == "CoAuthors");
    }

    [Fact]
    public void Validate_BreakingWithoutDescription_IsWarning()
    {
        var draft = new ConventionalCommitDraft { Type = "feat", Description = "add a thing", Breaking = true };
        var issues = ConventionalCommitBuilder.Validate(draft);
        Assert.Contains(issues, i => !i.IsError && i.Field == "BreakingDescription");
    }

    [Fact]
    public void Validate_CleanImperativeSubject_HasNoIssues()
    {
        var draft = new ConventionalCommitDraft { Type = "feat", Scope = "ui", Description = "add dark mode" };
        Assert.Empty(ConventionalCommitBuilder.Validate(draft));
    }

    // ---- Parse + round-trip ----

    [Fact]
    public void Parse_RecoversHeaderBodyAndTrailers()
    {
        var message =
            "feat(api)!: add pagination\n\n" +
            "Adds cursor pagination.\n\n" +
            "BREAKING CHANGE: v1 removed\n" +
            "Closes #42\n" +
            "Co-authored-by: Jane <jane@example.com>";

        var d = ConventionalCommitBuilder.Parse(message);

        Assert.Equal("feat", d.Type);
        Assert.Equal("api", d.Scope);
        Assert.Equal("add pagination", d.Description);
        Assert.Equal("Adds cursor pagination.", d.Body);
        Assert.True(d.Breaking);
        Assert.Equal("v1 removed", d.BreakingDescription);
        Assert.Equal(new[] { "#42" }, d.ClosesIssues);
        Assert.Equal(new[] { "Jane <jane@example.com>" }, d.CoAuthors);
    }

    [Fact]
    public void Parse_NonConventionalSubject_YieldsEmptyTypeCarryingWholeSubject()
    {
        var d = ConventionalCommitBuilder.Parse("just a plain message");
        Assert.Equal("", d.Type);
        Assert.Equal("just a plain message", d.Description);
    }

    [Fact]
    public void ParseBuild_RoundTrip_RecoversStableFields()
    {
        var original = new ConventionalCommitDraft
        {
            Type = "fix",
            Scope = "core",
            Description = "guard against nulls",
            Body = "The parser now short-circuits on an empty input.",
            Breaking = true,
            BreakingDescription = "empty input now returns null",
            CoAuthors = new[] { "Ann <ann@x.io>", "Bo <bo@y.io>" },
            ClosesIssues = new[] { "#3", "org/repo#8" },
        };

        var round = ConventionalCommitBuilder.Parse(ConventionalCommitBuilder.Build(original));

        Assert.Equal(original.Type, round.Type);
        Assert.Equal(original.Scope, round.Scope);
        Assert.Equal(original.Description, round.Description);
        Assert.Equal(original.Body, round.Body);
        Assert.Equal(original.Breaking, round.Breaking);
        Assert.Equal(original.BreakingDescription, round.BreakingDescription);
        Assert.Equal(original.CoAuthors.ToArray(), round.CoAuthors.ToArray());
        Assert.Equal(original.ClosesIssues.ToArray(), round.ClosesIssues.ToArray());
    }

    [Fact]
    public void ParseBuild_RoundTrip_CrlfMessage_RecoversFields()
    {
        var original = new ConventionalCommitDraft { Type = "docs", Scope = "readme", Description = "clarify setup" };
        var crlf = ConventionalCommitBuilder.Build(original).Replace("\n", "\r\n");

        var round = ConventionalCommitBuilder.Parse(crlf);

        Assert.Equal("docs", round.Type);
        Assert.Equal("readme", round.Scope);
        Assert.Equal("clarify setup", round.Description);
    }
}
