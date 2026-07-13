using GitLoom.Core.Agents;
using Xunit;

namespace GitLoom.Tests;

/// <summary>P2-06 pure hasher: normalization (case/slashes/trailing) and Unicode stability.</summary>
public sealed class RepoPathHasherTests
{
    [Fact]
    public void Hash_CaseSlashesAndTrailingSeparator_MapToOneHash()
    {
        var a = RepoPathHasher.Hash(@"C:\Repo\");
        var b = RepoPathHasher.Hash("c:/repo");
        var c = RepoPathHasher.Hash(@"C:\repo");
        var d = RepoPathHasher.Hash("c:/repo/");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.Equal(a, d);
    }

    [Fact]
    public void Hash_IsLowercaseHexSha256()
    {
        var hash = RepoPathHasher.Hash(@"C:\repos\project");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void Hash_UnicodePath_IsStableAndDistinct()
    {
        var unicode1 = RepoPathHasher.Hash(@"C:\repos\Ünï cödé repo");
        var unicode2 = RepoPathHasher.Hash(@"c:/repos/ünï cödé repo/");
        var other = RepoPathHasher.Hash(@"C:\repos\other");

        // Same repo (case/slash/trailing variants) → one hash; different repo → different hash.
        Assert.Equal(unicode1, unicode2);
        Assert.NotEqual(unicode1, other);
    }

    [Fact]
    public void Hash_UnixStyleTempPath_Works()
    {
        // The Linux CI leg passes temp dirs as the "windows repo path".
        var hash = RepoPathHasher.Hash("/tmp/gitloom-dual-work-abc123");
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
