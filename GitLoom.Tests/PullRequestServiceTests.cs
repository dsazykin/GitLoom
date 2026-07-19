using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fakes;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-23 (service) — the <see cref="PullRequestService.IsSupported"/> host/token matrix over a fake
/// keyring + <see cref="GitHostDetector"/>, plus dispatch: the service parses owner/repo from the
/// origin remote once and routes to the GitHub provider over the injected <see cref="HttpClient"/>,
/// carrying the token only in the Authorization header. Unsupported hosts and missing tokens degrade
/// to typed results, never a crash. No live network.
/// </summary>
public class PullRequestServiceTests
{
    private const string Token = "ghp_service_matrix_token_xyz";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        private readonly string _body;
        public CapturingHandler(string body = "[]") => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    // A repo with a single configured origin URL and a temp keyring holding an optional per-host token.
    private static (string RepoPath, GitService Git, SecureKeyring Keyring) Arrange(
        TempRepoFixture fx, string originUrl, string? tokenHost = null)
    {
        var git = new GitService();
        git.AddRemote(fx.RepoPath, "origin", originUrl);

        var keyringDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitloom-pr-keyring-" + Guid.NewGuid().ToString("N"));
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
        var svc = new PullRequestService(git, keyring);
        Assert.True(svc.IsSupported(repo));
    }

    [Fact]
    public void IsSupported_GitHubWithoutToken_False()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: null);
        var svc = new PullRequestService(git, keyring);
        Assert.False(svc.IsSupported(repo));
    }

    [Fact]
    public void IsSupported_GitLabWithToken_False_NotImplementedYet()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://gitlab.com/group/project.git", tokenHost: "gitlab.com");
        var svc = new PullRequestService(git, keyring);
        Assert.False(svc.IsSupported(repo)); // stub provider is not implemented → unsupported
    }

    [Fact]
    public void IsSupported_UnknownHost_False()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://git.example.com/team/repo.git", tokenHost: "git.example.com");
        var svc = new PullRequestService(git, keyring);
        Assert.False(svc.IsSupported(repo));
    }

    [Fact]
    public void IsSupported_NoRemote_False()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var svc = new PullRequestService(git, new SecureKeyring(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitloom-pr-" + Guid.NewGuid().ToString("N"))));
        Assert.False(svc.IsSupported(fx.RepoPath));
    }

    [Fact]
    public void IsSupported_SshRemote_ResolvesGitHub()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "git@github.com:octocat/hello-world.git", tokenHost: "github.com");
        var svc = new PullRequestService(git, keyring);
        Assert.True(svc.IsSupported(repo));
    }

    [Fact]
    public async Task ListAsync_DispatchesToGitHub_WithParsedSlug_AndAuthHeader()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: "github.com");
        var handler = new CapturingHandler("[]");
        var svc = new PullRequestService(git, keyring, new HttpClient(handler));

        var items = await svc.ListAsync(repo, PullRequestState.Open, CancellationToken.None);

        Assert.Empty(items);
        Assert.NotNull(handler.Last);
        Assert.Contains("/repos/octocat/hello-world/pulls", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Last.Headers.Authorization?.Scheme);
        Assert.Equal(Token, handler.Last.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(Token, handler.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task Operation_WithNoToken_ThrowsAuthenticationRequired()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://github.com/octocat/hello-world.git", tokenHost: null);
        var svc = new PullRequestService(git, keyring, new HttpClient(new CapturingHandler()));

        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            svc.ListAsync(repo, PullRequestState.Open, CancellationToken.None));
        Assert.Equal("github.com", ex.Host);
    }

    [Fact]
    public async Task Operation_OnUnsupportedHost_ThrowsTyped()
    {
        using var fx = new TempRepoFixture();
        var (repo, git, keyring) = Arrange(fx, "https://gitlab.com/group/project.git", tokenHost: "gitlab.com");
        var svc = new PullRequestService(git, keyring, new HttpClient(new CapturingHandler()));

        await Assert.ThrowsAsync<GitOperationException>(() =>
            svc.ListAsync(repo, PullRequestState.Open, CancellationToken.None));
    }

    // ---- Owner/repo parsing (the seam the service relies on) -----------------------------------

    [Theory]
    [InlineData("https://github.com/octocat/hello-world.git", "octocat", "hello-world")]
    [InlineData("https://github.com/octocat/hello-world", "octocat", "hello-world")]
    [InlineData("git@github.com:octocat/hello-world.git", "octocat", "hello-world")]
    [InlineData("ssh://git@github.com/octocat/hello-world.git", "octocat", "hello-world")]
    [InlineData("https://gitlab.com/group/subgroup/project.git", "group/subgroup", "project")]
    public void ParseOwnerRepo_HandlesRemoteForms(string url, string owner, string repo)
    {
        var parsed = GitHostDetector.ParseOwnerRepo(url);
        Assert.NotNull(parsed);
        Assert.Equal(owner, parsed!.Value.Owner);
        Assert.Equal(repo, parsed.Value.Repo);
    }

    [Theory]
    [InlineData("C:/local/repo")]
    [InlineData("/home/user/repo")]
    [InlineData("https://github.com/onlyowner")]
    public void ParseOwnerRepo_RejectsNonRepoUrls(string url)
        => Assert.Null(GitHostDetector.ParseOwnerRepo(url));
}
