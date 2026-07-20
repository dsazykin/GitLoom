using System;
using Mainguard.Agents.Agents;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-06 host→daemon path translation (audit fix: the daemon runs on Linux, so the Windows repo
/// path the client sends must become <c>/mnt/&lt;drive&gt;/…</c> before git can open it — while the
/// Linux CI leg's native paths must pass through untouched).
/// </summary>
public sealed class HostPathTranslatorTests
{
    [Theory]
    [InlineData(@"C:\Users\me\repo", "/mnt/c/Users/me/repo")]
    [InlineData("C:/Users/me/repo", "/mnt/c/Users/me/repo")]
    [InlineData(@"d:\work\Ünï cödé repo", "/mnt/d/work/Ünï cödé repo")]
    [InlineData(@"E:\", "/mnt/e")]
    [InlineData("E:", "/mnt/e")]
    [InlineData(@"C:\a\b\", "/mnt/c/a/b/")]
    public void LinuxDaemon_WindowsDrivePath_TranslatesToMnt(string windowsPath, string expected)
    {
        Assert.Equal(expected, HostPathTranslator.ToDaemonOpenablePath(windowsPath, daemonIsWindows: false));
    }

    [Theory]
    [InlineData("/tmp/fixture-repo")]
    [InlineData("/home/gitloom/gitloom/repos/abc.git")]
    [InlineData("relative/test/path")]
    public void LinuxDaemon_NativeOrRelativePath_PassesThrough(string path)
    {
        Assert.Equal(path, HostPathTranslator.ToDaemonOpenablePath(path, daemonIsWindows: false));
    }

    [Theory]
    [InlineData(@"\\server\share\repo")]
    [InlineData("//server/share/repo")]
    public void LinuxDaemon_UncPath_IsRefusedTyped(string unc)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => HostPathTranslator.ToDaemonOpenablePath(unc, daemonIsWindows: false));
        Assert.Contains("UNC", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\me\repo")]
    [InlineData(@"\\wsl.localhost\GitLoomEnv\home\gitloom")]
    [InlineData("/tmp/x")]
    public void WindowsDaemon_LocalDev_EverythingPassesThrough(string path)
    {
        Assert.Equal(path, HostPathTranslator.ToDaemonOpenablePath(path, daemonIsWindows: true));
    }

    [Theory]
    [InlineData(@"C:\x", true)]
    [InlineData("c:/x", true)]
    [InlineData("C:", true)]
    [InlineData("Cx\\y", false)]
    [InlineData("/mnt/c/x", false)]
    [InlineData("1:\\x", false)]
    public void IsWindowsDrivePath_ClassifiesCorrectly(string path, bool expected)
    {
        Assert.Equal(expected, HostPathTranslator.IsWindowsDrivePath(path));
    }

    [Fact]
    public void EmptyPath_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(
            () => HostPathTranslator.ToDaemonOpenablePath("  ", daemonIsWindows: false));
    }
}
