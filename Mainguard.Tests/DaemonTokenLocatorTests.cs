using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.Daemon;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Audit fix #1: the daemon writes its session token where IT runs (inside the VM in the shipped
/// topology); the client must resolve across candidates instead of assuming its own OS's path. The
/// freshest-written candidate wins (the daemon rotates the token on every start, so the newest token
/// belongs to the daemon that most recently claimed the loopback port).
/// </summary>
public sealed class DaemonTokenLocatorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gitloom-tokloc-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string content, DateTime lastWriteUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    [Fact]
    public void CandidatePaths_AlwaysIncludeTheLocalPerUserFile()
    {
        Assert.Contains(DaemonPaths.TokenFilePath(), DaemonTokenLocator.CandidatePaths());
    }

    [Fact]
    public void VmTokenUncPath_PointsIntoGitLoomEnvHome()
    {
        Assert.Equal(@"\\wsl.localhost\GitLoomEnv\home\gitloom\.gitloom\daemon.token",
            DaemonTokenLocator.VmTokenUncPath());
    }

    [Fact]
    public void CandidatePaths_OnWindows_IncludeTheVmUncBridge()
    {
        // The UNC candidate is what lets the shipped Windows client read the in-VM daemon's token.
        if (!OperatingSystem.IsWindows())
        {
            return; // the UNC bridge exists only on the Windows client
        }

        Assert.Contains(DaemonTokenLocator.VmTokenUncPath(), DaemonTokenLocator.CandidatePaths());
    }

    [Fact]
    public void TryReadToken_NoCandidateExists_ReturnsNull()
    {
        var candidates = new[] { Path.Combine(_dir, "missing-a"), Path.Combine(_dir, "missing-b") };

        Assert.Null(DaemonTokenLocator.TryReadToken(candidates));
    }

    [Fact]
    public void TryReadToken_SingleCandidate_ReadsAndTrims()
    {
        var path = Write("daemon.token", "  abc123\n", DateTime.UtcNow);

        Assert.Equal("abc123", DaemonTokenLocator.TryReadToken(new[] { path }));
    }

    [Fact]
    public void TryReadToken_MultipleCandidates_FreshestWins()
    {
        var stale = Write("local.token", "stale-local-daemon", DateTime.UtcNow.AddHours(-2));
        var fresh = Write("vm.token", "fresh-vm-daemon", DateTime.UtcNow);

        Assert.Equal("fresh-vm-daemon", DaemonTokenLocator.TryReadToken(new[] { stale, fresh }));
        // Order of the candidate list must not matter — freshness decides.
        Assert.Equal("fresh-vm-daemon", DaemonTokenLocator.TryReadToken(new[] { fresh, stale }));
    }

    [Fact]
    public void TryReadToken_EmptyTokenFile_IsNotAToken()
    {
        var empty = Write("empty.token", "   \n", DateTime.UtcNow);

        Assert.Null(DaemonTokenLocator.TryReadToken(new[] { empty }));
    }

    [Fact]
    public void ReadToken_NothingFound_ThrowsActionable_NamingEveryProbedPath()
    {
        var candidates = new[] { Path.Combine(_dir, "gone-a"), Path.Combine(_dir, "gone-b") };

        var ex = Assert.Throws<InvalidOperationException>(() => DaemonTokenLocator.ReadToken(candidates));
        Assert.All(candidates, c => Assert.Contains(c, ex.Message, StringComparison.Ordinal));
        Assert.Contains("setup", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
