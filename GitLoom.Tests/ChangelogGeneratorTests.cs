using System.Collections.Generic;
using GitLoom.Core.Analytics;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-28 (pure changelog) — pins <see cref="ChangelogGenerator.ParseSubject"/> across the conventional-commit
/// grammar (feat/fix/scope/`!`/BREAKING CHANGE/plain→other) and pins the exact grouped-markdown output of
/// <see cref="ChangelogGenerator.BuildNotes"/> (Breaking/Features/Fixes/Other + the Full-changelog range line).
/// The generator is pure — no IO — so the entire contract is byte-exact here.
/// </summary>
public class ChangelogGeneratorTests
{
    // ---- ParseSubject --------------------------------------------------------------------------

    [Fact]
    public void ParseSubject_Feat_NoScope()
    {
        var e = ChangelogGenerator.ParseSubject("deadbeefcafe", "feat: add releases panel");
        Assert.Equal("feat", e.Type);
        Assert.Equal("", e.Scope);
        Assert.Equal("add releases panel", e.Description);
        Assert.False(e.Breaking);
        Assert.Equal("deadbee", e.Sha); // truncated to 7
    }

    [Fact]
    public void ParseSubject_Fix_WithScope()
    {
        var e = ChangelogGenerator.ParseSubject("1234567abc", "fix(core): handle empty history");
        Assert.Equal("fix", e.Type);
        Assert.Equal("core", e.Scope);
        Assert.Equal("handle empty history", e.Description);
        Assert.False(e.Breaking);
    }

    [Fact]
    public void ParseSubject_BangMarksBreaking()
    {
        var e = ChangelogGenerator.ParseSubject("abc", "feat(api)!: drop v1 endpoints");
        Assert.Equal("feat", e.Type);
        Assert.Equal("api", e.Scope);
        Assert.True(e.Breaking);
    }

    [Fact]
    public void ParseSubject_BreakingChangeMarker_SetsBreaking()
    {
        var e = ChangelogGenerator.ParseSubject("abc", "chore: rework BREAKING CHANGE in config");
        Assert.Equal("chore", e.Type);
        Assert.True(e.Breaking);
    }

    [Fact]
    public void ParseSubject_TypeLowercased()
    {
        var e = ChangelogGenerator.ParseSubject("abc", "FEAT: shout");
        Assert.Equal("feat", e.Type);
    }

    [Fact]
    public void ParseSubject_PlainSubject_BecomesOther_NeverDropped()
    {
        var e = ChangelogGenerator.ParseSubject("f00ba12345", "Update the README file");
        Assert.Equal("other", e.Type);
        Assert.Equal("", e.Scope);
        Assert.Equal("Update the README file", e.Description);
        Assert.False(e.Breaking);
    }

    [Fact]
    public void ParseSubject_ShortSha_Preserved()
    {
        var e = ChangelogGenerator.ParseSubject("abc12", "feat: x");
        Assert.Equal("abc12", e.Sha);
    }

    // ---- BuildNotes (pinned exactly) -----------------------------------------------------------

    [Fact]
    public void BuildNotes_GroupsAndFormats_Exactly()
    {
        var entries = new List<ChangelogEntry>
        {
            new() { Type = "feat", Scope = "",     Description = "add releases panel", Sha = "aaaaaaa", Breaking = false },
            new() { Type = "fix",  Scope = "core", Description = "npe on empty repo",  Sha = "bbbbbbb", Breaking = false },
            new() { Type = "feat", Scope = "api",  Description = "drop v1 endpoints",  Sha = "ccccccc", Breaking = true  },
            new() { Type = "chore",Scope = "",     Description = "bump dependencies",  Sha = "ddddddd", Breaking = false },
        };

        var notes = ChangelogGenerator.BuildNotes(entries, "v1.0.0", "v1.1.0");

        const string expected =
            "### Breaking Changes\n" +
            "- **api:** drop v1 endpoints (ccccccc)\n" +
            "\n" +
            "### Features\n" +
            "- add releases panel (aaaaaaa)\n" +
            "\n" +
            "### Fixes\n" +
            "- **core:** npe on empty repo (bbbbbbb)\n" +
            "\n" +
            "### Other\n" +
            "- bump dependencies (ddddddd)\n" +
            "\n" +
            "**Full changelog:** v1.0.0...v1.1.0";

        Assert.Equal(expected, notes);
    }

    [Fact]
    public void BuildNotes_NoPreviousTag_FooterHasOnlyNewTag()
    {
        var entries = new List<ChangelogEntry>
        {
            new() { Type = "feat", Scope = "", Description = "first feature", Sha = "1111111", Breaking = false },
        };

        var notes = ChangelogGenerator.BuildNotes(entries, previousTag: null, newTag: "v1.0.0");

        const string expected =
            "### Features\n" +
            "- first feature (1111111)\n" +
            "\n" +
            "**Full changelog:** v1.0.0";

        Assert.Equal(expected, notes);
    }

    [Fact]
    public void BuildNotes_Empty_ReturnsEmptyString_NoThrow()
    {
        Assert.Equal("", ChangelogGenerator.BuildNotes(new List<ChangelogEntry>(), "v1", "v2"));
    }

    [Fact]
    public void BuildNotes_BreakingEntry_NotDoubleListedInFeatures()
    {
        var entries = new List<ChangelogEntry>
        {
            new() { Type = "feat", Scope = "", Description = "breaking feat", Sha = "aaaaaaa", Breaking = true },
        };

        var notes = ChangelogGenerator.BuildNotes(entries, null, "v2.0.0");

        Assert.Contains("### Breaking Changes", notes);
        Assert.DoesNotContain("### Features", notes);
    }
}
