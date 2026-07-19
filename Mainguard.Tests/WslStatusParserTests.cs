using System;
using System.IO;
using System.Text;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace Mainguard.Tests;

// TI-P2-21 #1 / plan §6 #1: WslStatusParser_VersionMatrix — the pure parser against checked-in
// captured `wsl --status`/`--version` outputs stored as raw UTF-16LE bytes (mirroring wsl.exe's real
// stream), decoded exactly as WslRunner does before classification.
public class WslStatusParserTests
{
    [Theory]
    [InlineData("not-installed.txt", null, WslInstallState.NotInstalled)]
    [InlineData("not-enabled.txt", null, WslInstallState.NotInstalled)]
    [InlineData("wsl1-only.txt", null, WslInstallState.Wsl1Only)]
    [InlineData("wsl1-default-german.txt", null, WslInstallState.Wsl1Only)]
    [InlineData("wsl2-ready-status.txt", null, WslInstallState.Wsl2Ready)]
    [InlineData("wsl2-ready-status.txt", "wsl2-ready-version.txt", WslInstallState.Wsl2Ready)]
    [InlineData("needs-kernel-update.txt", null, WslInstallState.NeedsKernelUpdate)]
    public void WslStatusParser_VersionMatrix(string statusFixture, string? versionFixture, WslInstallState expected)
    {
        var status = ReadFixture(statusFixture);
        var version = versionFixture is null ? null : ReadFixture(versionFixture);

        var report = WslStatusParser.Parse(status, version);

        Assert.Equal(expected, report.State);
        Assert.Equal(expected == WslInstallState.Wsl2Ready, report.IsReady);
    }

    [Fact]
    public void WslStatusParser_Wsl2Ready_CapturesVersionFields()
    {
        var status = ReadFixture("wsl2-ready-status.txt");
        var version = ReadFixture("wsl2-ready-version.txt");

        var report = WslStatusParser.Parse(status, version);

        Assert.Equal("2", report.DefaultVersion);
        Assert.Equal("2.0.9.0", report.WslVersion);
        Assert.Equal("5.15.133.1-1", report.KernelVersion);
    }

    [Fact]
    public void WslStatusParser_HardNonZeroExit_NoFields_IsNotInstalled()
    {
        // 'wsl' is not recognized as an internal or external command — cmd.exe surfaces this, exit 1.
        var report = WslStatusParser.Parse("'wsl' is not recognized as an internal or external command",
            versionOutput: null, exitCode: 1);
        Assert.Equal(WslInstallState.NotInstalled, report.State);
    }

    [Fact]
    public void WslStatusParser_Unrecognized_IsUnknown_NotSilentPass()
    {
        var report = WslStatusParser.Parse("some totally unexpected output shape");
        Assert.Equal(WslInstallState.Unknown, report.State);
        Assert.False(report.IsReady);
    }

    private static string ReadFixture(string name)
    {
        var path = Path.Combine(RepoRoot(), "Mainguard.Tests", "Fixtures", "WslStatus", name);
        // Fixtures are raw UTF-16LE bytes (as wsl.exe emits); decode the same way WslRunner does.
        var bytes = File.ReadAllBytes(path);
        return Encoding.Unicode.GetString(bytes);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return dir ?? AppContext.BaseDirectory;
    }
}
