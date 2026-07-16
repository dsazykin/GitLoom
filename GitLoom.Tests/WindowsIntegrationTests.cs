using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;
using GitLoom.Tests.TestTools;

namespace GitLoom.Tests;

/// <summary>TI-P2-22 #7: shell-integration registry keys — per-user only, install/uninstall symmetry,
/// and (WindowsOnly) a real reg.exe round-trip.</summary>
public class WindowsIntegrationTests
{
    [Fact]
    public void AllKeys_ShouldBePerUser_NeverHklm()
    {
        var install = WindowsIntegration.InstallCommands(@"C:\Program Files\GitLoom\GitLoom.exe");
        var uninstall = WindowsIntegration.UninstallCommands();

        foreach (var cmd in install.Concat(uninstall))
        {
            var key = cmd[1]; // reg add/delete <key> ...
            Assert.StartsWith(@"HKCU\Software\Classes", key);
            Assert.DoesNotContain("HKLM", key);
        }
    }

    [Fact]
    public void Install_ShouldRegisterContextMenu_AndProtocolHandler()
    {
        var install = WindowsIntegration.InstallCommands(@"C:\gl\GitLoom.exe");
        var keys = install.Select(c => c[1]).ToArray();

        Assert.Contains(keys, k => k.EndsWith(@"Directory\shell\GitLoom"));
        Assert.Contains(keys, k => k.EndsWith(@"Directory\Background\shell\GitLoom"));
        Assert.Contains(keys, k => k.EndsWith(@"Classes\gitloom"));
        Assert.Contains(keys, k => k.EndsWith(@"gitloom\shell\open\command"));
        // The exe is embedded in the command values, quoted, with the %1 / %V placeholders.
        Assert.Contains(install, c => c.Any(a => a.Contains("GitLoom.exe") && a.Contains("%1")));
    }

    [Fact]
    public void Uninstall_ShouldRemoveEveryKeyRootInstallWrote()
    {
        var installKeys = WindowsIntegration.InstallCommands(@"C:\gl\GitLoom.exe").Select(c => c[1]);
        var owned = WindowsIntegration.OwnedKeyRoots();
        // Every install key sits under one of the owned roots the uninstall tree-deletes.
        foreach (var key in installKeys)
            Assert.Contains(owned, root => key.StartsWith(root, StringComparison.Ordinal));

        var deleted = WindowsIntegration.UninstallCommands().Select(c => c[1]).ToArray();
        Assert.Equal(owned.OrderBy(x => x), deleted.OrderBy(x => x));
    }

    [WindowsOnlyFact]
    public async Task RegExe_InstallThenUninstall_ShouldRoundTrip()
    {
        var runner = new RegExeRegistryCommandRunner();
        var ct = CancellationToken.None;
        const string testRoot = @"HKCU\Software\GitLoom\_p2_22_test\Classes";
        var protocolKey = $@"{testRoot}\gitloom";

        try
        {
            foreach (var cmd in WindowsIntegration.InstallCommands(@"C:\gl\GitLoom.exe", testRoot))
                Assert.True(await runner.RunAsync(cmd, ct), $"reg {string.Join(' ', cmd)} failed");

            Assert.True(await runner.RunAsync(new[] { "query", protocolKey }, ct), "protocol key should exist after install");

            foreach (var cmd in WindowsIntegration.UninstallCommands(testRoot))
                await runner.RunAsync(cmd, ct);

            Assert.False(await runner.RunAsync(new[] { "query", protocolKey }, ct), "protocol key should be gone after uninstall");
        }
        finally
        {
            await runner.RunAsync(new[] { "delete", @"HKCU\Software\GitLoom\_p2_22_test", "/f" }, ct);
        }
    }
}
