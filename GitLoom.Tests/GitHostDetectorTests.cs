using GitLoom.Core.Models;
using GitLoom.Core.Security;
using Xunit;

namespace GitLoom.Tests;

// Regression for audit 1.7: multi-host credential detection. Tokens are keyed by
// host and the right username convention is chosen per provider (and, crucially,
// tokens are fed via git's credential mechanism, not embedded in the URL/argv).
public class GitHostDetectorTests
{
    [Theory]
    [InlineData("https://github.com/acme/repo.git", "github.com", HostKind.GitHub)]
    [InlineData("git@github.com:acme/repo.git", "github.com", HostKind.GitHub)]
    [InlineData("https://gitlab.com/acme/repo.git", "gitlab.com", HostKind.GitLab)]
    [InlineData("git@gitlab.com:acme/repo.git", "gitlab.com", HostKind.GitLab)]
    [InlineData("https://bitbucket.org/acme/repo.git", "bitbucket.org", HostKind.Bitbucket)]
    [InlineData("https://dev.azure.com/acme/proj/_git/repo", "dev.azure.com", HostKind.AzureDevOps)]
    [InlineData("https://git.internal.corp/acme/repo.git", "git.internal.corp", HostKind.Unknown)]
    public void Detect_IdentifiesHostAndKind(string url, string expectedHost, HostKind expectedKind)
    {
        var (host, kind) = GitHostDetector.Detect(url);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedKind, kind);
    }

    [Theory]
    [InlineData(HostKind.GitHub, "x-access-token")]
    [InlineData(HostKind.GitLab, "oauth2")]
    [InlineData(HostKind.Bitbucket, "x-token-auth")]
    [InlineData(HostKind.AzureDevOps, "token")]
    public void UsernameForToken_MatchesHostConvention(HostKind kind, string expected)
    {
        Assert.Equal(expected, GitHostDetector.UsernameForToken(kind));
    }

    [Theory]
    [InlineData(@"C:\repo")]
    [InlineData("C:/repo")]
    [InlineData(@"D:\work\project")]
    [InlineData(@"\\server\share\repo")]
    public void Detect_DoesNotMisclassifyLocalPathAsRemote(string path)
    {
        // A Windows drive / UNC path must not be read as an scp-like remote host.
        var (host, kind) = GitHostDetector.Detect(path);
        Assert.NotEqual("c", host.ToLowerInvariant());
        Assert.Equal(HostKind.Unknown, kind);
    }

    [Fact]
    public void TokenKeyForHost_IsFileSystemSafe()
    {
        // ':' would be an invalid filename on Windows (the keyring is file-backed).
        var key = GitHostDetector.TokenKeyForHost("github.com");
        Assert.DoesNotContain(':', key);
        Assert.Equal("token_github.com", key);
    }

    [Theory]
    [InlineData("GitHub.COM", "token_github.com")]                 // case-insensitive
    [InlineData("git.internal.corp:8443", "token_git.internal.corp_8443")] // port ':' sanitized
    public void TokenKeyForHost_NormalizesAndSanitizes(string host, string expected)
    {
        var key = GitHostDetector.TokenKeyForHost(host);
        Assert.Equal(expected, key);
        Assert.DoesNotContain(':', key);
    }
}
