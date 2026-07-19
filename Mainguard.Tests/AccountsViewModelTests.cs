using System;
using System.IO;
using System.Linq;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.Git.Sync;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

// T-14: Accounts page offline behavior — known-host catalog, PAT store/remove keyed
// token_<host>, and status reflecting stored tokens. Live device-flow is deferred.
public class AccountsViewModelTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-acct-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    [Fact]
    public void Ctor_ListsKnownHosts_WithProviderMetadata()
    {
        using var dir = new TempDir();
        var vm = new AccountsViewModel(new SecureKeyring(dir.Path));

        // GitHub keeps the device flow; GitLab (Q1) now routes through loopback OAuth (browser); the
        // rest are PAT hosts. The auth-method label reflects each.
        Assert.Contains(vm.Accounts, a => a.Host == "github.com" && a.Kind == HostKind.GitHub
            && a.AuthMethod == HostAuthMethod.OAuthDeviceFlow && a.SupportsDeviceFlow
            && a.AuthMethodLabel == "OAuth device flow");
        Assert.Contains(vm.Accounts, a => a.Host == "gitlab.com" && a.Kind == HostKind.GitLab
            && a.AuthMethod == HostAuthMethod.OAuthLoopback && !a.SupportsDeviceFlow
            && a.AuthMethodLabel == "OAuth (browser)");
        Assert.Contains(vm.Accounts, a => a.Host == "bitbucket.org"
            && a.AuthMethod == HostAuthMethod.PersonalAccessToken && !a.SupportsDeviceFlow
            && a.AuthMethodLabel == "Personal access token");
        Assert.Contains(vm.Accounts, a => a.Host == "dev.azure.com"
            && a.AuthMethod == HostAuthMethod.PersonalAccessToken && !a.SupportsDeviceFlow);
        Assert.All(vm.Accounts, a => Assert.False(a.HasToken));
    }

    [Fact]
    public void SavePat_StoresTokenUnderHostKey_AndFlipsStatus()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        var vm = new AccountsViewModel(keyring);

        var bitbucket = vm.Accounts.First(a => a.Host == "bitbucket.org");
        bitbucket.PatInput = "pasted-token";
        bitbucket.SavePatCommand.Execute(null);

        // Stored under the landed keyring convention token_<host>.
        Assert.Equal("pasted-token", keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost("bitbucket.org")));

        // Reloaded row reflects the signed-in status.
        var refreshed = vm.Accounts.First(a => a.Host == "bitbucket.org");
        Assert.True(refreshed.HasToken);
        Assert.Equal("Signed in", refreshed.StatusLabel);
    }

    [Fact]
    public void SignOut_RemovesStoredToken()
    {
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret(GitHostDetector.TokenKeyForHost("github.com"), "ghp_x");
        var vm = new AccountsViewModel(keyring);

        var github = vm.Accounts.First(a => a.Host == "github.com");
        Assert.True(github.HasToken);
        github.SignOutCommand.Execute(null);

        Assert.Null(keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost("github.com")));
        Assert.False(vm.Accounts.First(a => a.Host == "github.com").HasToken);
    }

    [Fact]
    public void AddCustomHost_AppendsPatRow()
    {
        using var dir = new TempDir();
        var vm = new AccountsViewModel(new SecureKeyring(dir.Path)) { NewHost = "git.internal.corp" };
        vm.AddCustomHostCommand.Execute(null);

        Assert.Contains(vm.Accounts, a => a.Host == "git.internal.corp");
    }
}
