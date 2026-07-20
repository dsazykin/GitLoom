using System.Linq;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

// Issue #71: group branches by subfolder within each sidebar section. The tree builder is pure
// (no repo) — strings in, tree out.
public class BranchTreeBuilderTests
{
    [Fact]
    public void Build_FlatNames_ShouldAllBeTopLevelLeaves()
    {
        var roots = BranchTreeBuilder.Build(new[] { "main", "develop" });

        Assert.Equal(2, roots.Count);
        Assert.All(roots, n => Assert.False(n.IsFolder));
        Assert.Equal("main", roots[0].Name);
        Assert.Equal("main", roots[0].FullName);
        Assert.Equal("develop", roots[1].Name);
        Assert.Equal("develop", roots[1].FullName);
    }

    [Fact]
    public void Build_SharedPrefix_ShouldGroupUnderOneFolderNode()
    {
        var roots = BranchTreeBuilder.Build(new[] { "feature/foo", "feature/bar" });

        var folder = Assert.Single(roots);
        Assert.True(folder.IsFolder);
        Assert.Equal("feature", folder.Name);
        Assert.Null(folder.FullName);
        Assert.Equal(2, folder.Children.Count);
        Assert.Equal("foo", folder.Children[0].Name);
        Assert.Equal("feature/foo", folder.Children[0].FullName);
        Assert.False(folder.Children[0].IsFolder);
        Assert.Equal("bar", folder.Children[1].Name);
        Assert.Equal("feature/bar", folder.Children[1].FullName);
    }

    [Fact]
    public void Build_MixedTopLevelAndGrouped_ShouldKeepBothShapes()
    {
        var roots = BranchTreeBuilder.Build(new[] { "main", "feature/foo" });

        Assert.Equal(2, roots.Count);
        Assert.False(roots[0].IsFolder);
        Assert.Equal("main", roots[0].Name);
        Assert.True(roots[1].IsFolder);
        Assert.Equal("feature", roots[1].Name);
        Assert.Single(roots[1].Children);
    }

    [Fact]
    public void Build_DeeplyNestedNames_ShouldRecurseMultipleLevels()
    {
        var roots = BranchTreeBuilder.Build(new[] { "release/2026/q1", "release/2026/q2" });

        var release = Assert.Single(roots);
        Assert.True(release.IsFolder);
        Assert.Equal("release", release.Name);

        var year = Assert.Single(release.Children);
        Assert.True(year.IsFolder);
        Assert.Equal("2026", year.Name);

        Assert.Equal(2, year.Children.Count);
        Assert.Equal("q1", year.Children[0].Name);
        Assert.Equal("release/2026/q1", year.Children[0].FullName);
        Assert.Equal("q2", year.Children[1].Name);
        Assert.Equal("release/2026/q2", year.Children[1].FullName);
    }

    [Fact]
    public void Build_PreservesInputOrder_WithinEachLevel()
    {
        var roots = BranchTreeBuilder.Build(new[] { "zeta", "feature/b", "alpha", "feature/a" });

        Assert.Equal(new[] { "zeta", "feature", "alpha" }, roots.Select(n => n.Name));
        var featureFolder = roots.Single(n => n.Name == "feature");
        Assert.Equal(new[] { "b", "a" }, featureFolder.Children.Select(n => n.Name));
    }

    [Fact]
    public void Build_EmptyInput_ShouldReturnEmpty()
    {
        Assert.Empty(BranchTreeBuilder.Build(System.Array.Empty<string>()));
    }

    [Fact]
    public void Build_DuplicateFolderSegmentAcrossCalls_ShouldReuseSameFolderNode()
    {
        // Three branches share the "feature" prefix; the folder node must appear once, not three times.
        var roots = BranchTreeBuilder.Build(new[] { "feature/a", "main", "feature/b", "feature/c" });

        Assert.Equal(1, roots.Count(n => n.IsFolder && n.Name == "feature"));
        var folder = roots.Single(n => n.Name == "feature");
        Assert.Equal(3, folder.Children.Count);
    }
}
