using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Security;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-26 (service) — the <see cref="CheckStatusService.IsSupported"/> host/token matrix over a real repo +
/// a temp <see cref="SecureKeyring"/>, plus dispatch: the service parses owner/repo from the origin remote
/// once (through the shared <see cref="HostConnectionResolver"/>, the same path as the PR/issue services)
/// and routes to the GitHub provider over the injected <see cref="HttpClient"/>, carrying the token only in
/// the Authorization header. Unsupported hosts and missing tokens degrade to typed results. No live network.
/// </summary>
public class CheckStatusServiceTests
{
    private const string Token = "ghp_checks_service_matrix_token_xyz";
    private const string Sha = "abc123";

    // Routes check-runs vs status to empty fixtures so a dispatched GetChecksAsync returns cleanly.
    private sealed class TwoSurfaceHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            var body = request.RequestUri!.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal)
                ? "{\"total_count\":0,\"check_runs\":[]}"
                : "{\"state\":\"pending\",\"statuses\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (string RepoPath, GitService Git, SecureKeyring Keyring) Arrange(
        TempRepoFixture fx, string originUrl, string? tokenHost = null)
    {
        var git = new GitService();
        git.AddRemote(fx.RepoPath, "origin", originUrl);

        var keyringDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitloom-checks-keyring-" + Guid.NewGuid().ToString("N"));
        var keyring = new SecureKeyring(keyringDir);
        if (tokenHost is not null)
            keyring.SaveSecret(GitHostDetector.TokenKeyForHost(tokenHost), Token);
        return (fx.RepoPath, git, keyring);
    }

    [Fact]
    public void IsSupported_GitHubWithToken_True()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: "github.com");
        Assert.True(new CheckStatusService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_GitHubWithoutToken_False()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: null);
        Assert.False(new CheckStatusService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_GitLabWithToken_False_NotImplementedYet()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://gitlab.com/group/project.git", tokenHost: "gitlab.com");
        Assert.False(new CheckStatusService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_UnknownHost_False()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://git.example.com/team/repo.git", tokenHost: "git.example.com");
        Assert.False(new CheckStatusService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_NoRemote_False()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var svc = new CheckStatusService(git, new SecureKeyring(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitloom-checks-" + Guid.NewGuid().ToString("N"))));
        Assert.False(svc.IsSupported(fx.RepoPath));
    }

    [Fact]
    public void IsSupported_SshRemote_ResolvesGitHub()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "git@github.com:octocat/hello-world.git", tokenHost: "github.com");
        Assert.True(new CheckStatusService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public async Task GetChecksAsync_DispatchesToGitHub_WithParsedSlug_AndAuthHeader()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: "github.com");
        var handler = new TwoSurfaceHandler();
        var svc = new CheckStatusService(git, keyring, new HttpClient(handler));

        var checks = await svc.GetChecksAsync(repo, Sha, CancellationToken.None);

        Assert.False(checks.HasAny);
        Assert.NotNull(handler.Last);
        Assert.Contains("/repos/octocat/hello-world/commits/", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Last.Headers.Authorization?.Scheme);
        Assert.Equal(Token, handler.Last.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(Token, handler.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Operation_WithNoToken_ThrowsAuthenticationRequired()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: null);
        var svc = new CheckStatusService(git, keyring, new HttpClient(new TwoSurfaceHandler()));

        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            svc.GetChecksAsync(repo, Sha, CancellationToken.None));
        Assert.Equal("github.com", ex.Host);
    }

    [Fact]
    public async Task Operation_OnUnsupportedHost_ThrowsTyped()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://gitlab.com/group/project.git", tokenHost: "gitlab.com");
        var svc = new CheckStatusService(git, keyring, new HttpClient(new TwoSurfaceHandler()));

        await Assert.ThrowsAsync<GitOperationException>(() =>
            svc.GetChecksAsync(repo, Sha, CancellationToken.None));
    }
}
