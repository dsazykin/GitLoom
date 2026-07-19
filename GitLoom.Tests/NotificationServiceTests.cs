using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Security;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-27 (service) — the <see cref="NotificationService.IsSupported"/> host/token matrix over a real repo +
/// a temp <see cref="SecureKeyring"/>, plus dispatch: the service resolves the origin host + token through
/// the shared <see cref="HostConnectionResolver"/> (the same path as the PR/issue/checks services) and
/// routes to the GitHub provider over the injected <see cref="HttpClient"/>, carrying the token only in the
/// Authorization header. Unsupported hosts and missing tokens degrade to typed results. No live network.
/// </summary>
public class NotificationServiceTests
{
    private const string Token = "ghp_notifications_service_matrix_token_xyz";

    private sealed class ListHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (string RepoPath, GitService Git, SecureKeyring Keyring) Arrange(
        TempRepoFixture fx, string originUrl, string? tokenHost = null)
    {
        var git = new GitService();
        git.AddRemote(fx.RepoPath, "origin", originUrl);

        var keyringDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitloom-notif-keyring-" + Guid.NewGuid().ToString("N"));
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
        Assert.True(new NotificationService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_GitHubWithoutToken_False()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: null);
        Assert.False(new NotificationService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_GitLabWithToken_False_NotImplementedYet()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://gitlab.com/group/project.git", tokenHost: "gitlab.com");
        Assert.False(new NotificationService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_UnknownHost_False()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://git.example.com/team/repo.git", tokenHost: "git.example.com");
        Assert.False(new NotificationService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public void IsSupported_NoRemote_False()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var svc = new NotificationService(git, new SecureKeyring(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitloom-notif-" + Guid.NewGuid().ToString("N"))));
        Assert.False(svc.IsSupported(fx.RepoPath));
    }

    [Fact]
    public void IsSupported_SshRemote_ResolvesGitHub()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "git@github.com:octocat/hello-world.git", tokenHost: "github.com");
        Assert.True(new NotificationService(git, keyring).IsSupported(repo));
    }

    [Fact]
    public async Task ListAsync_DispatchesToGitHub_WithAuthHeader()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: "github.com");
        var handler = new ListHandler();
        var svc = new NotificationService(git, keyring, new HttpClient(handler));

        var items = await svc.ListAsync(repo, onlyUnread: true, CancellationToken.None);

        Assert.Empty(items);
        Assert.NotNull(handler.Last);
        Assert.Contains("/notifications", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Last.Headers.Authorization?.Scheme);
        Assert.Equal(Token, handler.Last.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(Token, handler.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Operation_WithNoToken_ThrowsAuthenticationRequired()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: null);
        var svc = new NotificationService(git, keyring, new HttpClient(new ListHandler()));

        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            svc.ListAsync(repo, onlyUnread: true, CancellationToken.None));
        Assert.Equal("github.com", ex.Host);
    }

    [Fact]
    public async Task Operation_OnUnsupportedHost_ThrowsTyped()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://gitlab.com/group/project.git", tokenHost: "gitlab.com");
        var svc = new NotificationService(git, keyring, new HttpClient(new ListHandler()));

        await Assert.ThrowsAsync<GitOperationException>(() =>
            svc.MarkAllReadAsync(repo, CancellationToken.None));
    }
}
