using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.Git.Sync;
using Xunit;

namespace GitLoom.Tests;

// TI-14 #1/#2: provider resolution keys off host + kind, and the token username is a
// single source of truth (GitHostDetector.UsernameForToken — the same member the real
// auth path RunGitCheckedAuthenticated consults). A per-provider username switch would
// diverge from that and is a rejection trigger.
public class HostProviderRegistryTests
{
    [Fact]
    public void Resolve_GitHubDotCom_ReturnsGitHubProvider()
    {
        var provider = HostProviderRegistry.Resolve("github.com", HostKind.GitHub);
        Assert.IsType<GitHubProvider>(provider);
        Assert.Equal(HostAuthMethod.OAuthDeviceFlow, provider.AuthMethod);
        Assert.True(provider.SupportsDeviceFlow); // GitHub stays on the device flow (Q1)
        Assert.Equal("github.com", provider.Host);
    }

    [Fact]
    public void Resolve_SelfHostedGitLab_ReturnsGitLabProvider()
    {
        // Kind = GitLab but host != gitlab.com (self-hosted, classified via a URL hint).
        var provider = HostProviderRegistry.Resolve("gitlab.internal.corp", HostKind.GitLab);
        Assert.IsType<GitLabProvider>(provider);
        // Q1: GitLab now routes through loopback OAuth (PKCE), not the device flow.
        Assert.Equal(HostAuthMethod.OAuthLoopback, provider.AuthMethod);
        Assert.False(provider.SupportsDeviceFlow);
        Assert.Equal("gitlab.internal.corp", provider.Host);
    }

    [Fact]
    public void Resolve_UnknownHost_ReturnsGenericProvider()
    {
        var provider = HostProviderRegistry.Resolve("git.example.org", HostKind.Unknown);
        Assert.IsType<GenericHostProvider>(provider);
        Assert.Equal(HostAuthMethod.PersonalAccessToken, provider.AuthMethod);
        Assert.False(provider.SupportsDeviceFlow); // PAT dialog v1
    }

    [Fact]
    public void Resolve_Bitbucket_ReturnsBitbucketPatProvider()
    {
        var provider = HostProviderRegistry.Resolve("bitbucket.org", HostKind.Bitbucket);
        Assert.IsType<BitbucketProvider>(provider);
        Assert.Equal(HostAuthMethod.PersonalAccessToken, provider.AuthMethod);
        Assert.False(provider.SupportsDeviceFlow);
    }

    [Fact]
    public void Resolve_AzureDevOps_ReturnsAzureDevOpsPatProvider()
    {
        var provider = HostProviderRegistry.Resolve("dev.azure.com", HostKind.AzureDevOps);
        Assert.IsType<AzureDevOpsProvider>(provider);
        Assert.Equal(HostAuthMethod.PersonalAccessToken, provider.AuthMethod);
        Assert.False(provider.SupportsDeviceFlow);
    }

    [Theory]
    [InlineData(HostKind.GitHub, "x-access-token")]
    [InlineData(HostKind.GitLab, "oauth2")]
    [InlineData(HostKind.Bitbucket, "x-token-auth")]
    [InlineData(HostKind.AzureDevOps, "token")]
    [InlineData(HostKind.Unknown, "x-access-token")]
    public void TokenUsername_ShouldMatchHostConvention_SingleSource(HostKind kind, string expected)
    {
        var provider = HostProviderRegistry.Resolve("any.host", kind);
        // The provider must delegate to the exact same member the auth path uses.
        Assert.Equal(GitHostDetector.UsernameForToken(kind), provider.TokenUsername);
        Assert.Equal(expected, provider.TokenUsername);
    }

    [Fact]
    public async System.Threading.Tasks.Task PatProvider_UsesPromptCallback_ToAcquireToken()
    {
        var ctx = new HostAuthContext
        {
            PromptForPat = (host, ct) => System.Threading.Tasks.Task.FromResult<string?>("pasted-pat")
        };
        var provider = HostProviderRegistry.Resolve("bitbucket.org", HostKind.Bitbucket, ctx);
        var token = await provider.AcquireTokenAsync(default);
        Assert.Equal("pasted-pat", token);
    }

    [Fact]
    public async System.Threading.Tasks.Task PatProvider_NoPrompt_ThrowsAuthRequiredWithHost()
    {
        var provider = HostProviderRegistry.Resolve("bitbucket.org", HostKind.Bitbucket);
        var ex = await Assert.ThrowsAsync<Mainguard.Git.Exceptions.AuthenticationRequiredException>(
            () => provider.AcquireTokenAsync(default));
        Assert.Equal("bitbucket.org", ex.Host);
    }
}
