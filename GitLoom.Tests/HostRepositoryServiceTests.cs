using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Security;
using GitLoom.Core.Services;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-48 — drives <see cref="HostRepositoryService"/> and its per-host providers against JSON fixtures
/// through an injected <see cref="HttpMessageHandler"/> (no live network). Asserts GitHub and GitLab
/// listings map to the host-agnostic <see cref="RemoteRepository"/> model, the correct per-host endpoint
/// is hit, unimplemented hosts report unsupported, token resolution keys off <c>token_&lt;host&gt;</c>
/// (with the GitHub <c>github_token</c> fallback), and — critically (G-4) — the token appears ONLY in
/// the Authorization header, never in a produced model string, a request URL, or an exception message.
/// </summary>
public class HostRepositoryServiceTests
{
    private const string Token = "glpat_SeNtInEl_TOKEN_do_not_leak_123";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _responder;
        public readonly List<HttpRequestMessage> Requests = new();

        public StubHandler(HttpStatusCode status, string body) : this(_ => (status, body)) { }
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (status, body) = _responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        public HttpRequestMessage Last => Requests[^1];
    }

    private sealed class TempKeyring : IDisposable
    {
        public SecureKeyring Keyring { get; }
        private readonly string _dir;
        public TempKeyring()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gitloom-hostrepo-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Keyring = new SecureKeyring(_dir);
        }
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
    }

    private static void AssertTokenOnlyInAuthHeader(StubHandler handler, IEnumerable<string> producedStrings)
    {
        foreach (var req in handler.Requests)
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            Assert.DoesNotContain(Token, req.RequestUri!.ToString());
        }
        foreach (var s in producedStrings)
            Assert.DoesNotContain(Token, s ?? "");
    }

    // ---- GitHub ----------------------------------------------------------------------------------

    [Fact]
    public async Task GitHub_ListsAndMaps_UserRepos_ToHostAgnosticModel()
    {
        using var tk = new TempKeyring();
        tk.Keyring.SaveSecret(GitHostDetector.TokenKeyForHost("github.com"), Token);

        const string body = """
        [
          { "name": "hello-world", "full_name": "octocat/hello-world", "private": false,
            "html_url": "https://github.com/octocat/hello-world", "clone_url": "https://github.com/octocat/hello-world.git",
            "description": "My first repo", "updated_at": "2026-06-01T10:00:00Z" },
          { "name": "secret", "full_name": "octocat/secret", "private": true,
            "html_url": "https://github.com/octocat/secret", "clone_url": "https://github.com/octocat/secret.git",
            "description": null, "updated_at": "2026-05-01T10:00:00Z" }
        ]
        """;
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var svc = new HostRepositoryService(tk.Keyring, new HttpClient(handler));

        var repos = await svc.ListMyRepositoriesAsync("github.com", HostKind.GitHub, CancellationToken.None);

        Assert.Equal(2, repos.Count);
        var first = repos[0];
        Assert.Equal(HostKind.GitHub, first.Kind);
        Assert.Equal("github.com", first.Host);
        Assert.Equal("hello-world", first.Name);
        Assert.Equal("octocat/hello-world", first.FullName);
        Assert.Equal("https://github.com/octocat/hello-world.git", first.CloneUrl);
        Assert.Equal("My first repo", first.Description);
        Assert.False(first.IsPrivate);
        Assert.True(repos[1].IsPrivate);
        Assert.Null(repos[1].Description);

        // Endpoint: /user/repos across all affiliations, capped at 100.
        Assert.Contains("/user/repos", handler.Last.RequestUri!.AbsolutePath);
        Assert.Contains("per_page=100", handler.Last.RequestUri!.Query);
        AssertTokenOnlyInAuthHeader(handler, repos.SelectMany(r => new[] { r.CloneUrl, r.FullName, r.Description ?? "" }));
    }

    [Fact]
    public async Task GitHub_LegacyGithubTokenFallback_IsUsed()
    {
        using var tk = new TempKeyring();
        tk.Keyring.SaveSecret("github_token", Token); // legacy key only, no token_github.com

        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var svc = new HostRepositoryService(tk.Keyring, new HttpClient(handler));

        Assert.True(svc.IsSupported("github.com", HostKind.GitHub));
        var repos = await svc.ListMyRepositoriesAsync("github.com", HostKind.GitHub, CancellationToken.None);
        Assert.Empty(repos);
        Assert.Equal(Token, handler.Last.Headers.Authorization?.Parameter);
    }

    // ---- GitLab ----------------------------------------------------------------------------------

    [Fact]
    public async Task GitLab_ListsAndMaps_Projects_ToHostAgnosticModel()
    {
        using var tk = new TempKeyring();
        tk.Keyring.SaveSecret(GitHostDetector.TokenKeyForHost("gitlab.com"), Token);

        const string body = """
        [
          { "path": "webapp", "path_with_namespace": "acme/webapp", "visibility": "private",
            "http_url_to_repo": "https://gitlab.com/acme/webapp.git", "web_url": "https://gitlab.com/acme/webapp",
            "description": "The web app", "last_activity_at": "2026-06-10T12:00:00Z" },
          { "path": "docs", "path_with_namespace": "acme/docs", "visibility": "public",
            "http_url_to_repo": "https://gitlab.com/acme/docs.git", "web_url": "https://gitlab.com/acme/docs",
            "description": "", "last_activity_at": "2026-06-05T12:00:00Z" }
        ]
        """;
        var handler = new StubHandler(HttpStatusCode.OK, body);
        var svc = new HostRepositoryService(tk.Keyring, new HttpClient(handler));

        var repos = await svc.ListMyRepositoriesAsync("gitlab.com", HostKind.GitLab, CancellationToken.None);

        Assert.Equal(2, repos.Count);
        var app = repos[0];
        Assert.Equal(HostKind.GitLab, app.Kind);
        Assert.Equal("gitlab.com", app.Host);
        Assert.Equal("webapp", app.Name);
        Assert.Equal("acme/webapp", app.FullName);
        Assert.Equal("https://gitlab.com/acme/webapp.git", app.CloneUrl);
        Assert.Equal("https://gitlab.com/acme/webapp", app.HtmlUrl);
        Assert.Equal("The web app", app.Description);
        Assert.True(app.IsPrivate);          // visibility=private
        Assert.False(repos[1].IsPrivate);    // visibility=public
        Assert.Null(repos[1].Description);   // empty string normalized to null

        // Endpoint: GitLab v4 /projects?membership=true ordered by last_activity_at, capped at 100.
        Assert.Contains("/api/v4/projects", handler.Last.RequestUri!.AbsolutePath);
        Assert.Contains("membership=true", handler.Last.RequestUri!.Query);
        Assert.Contains("order_by=last_activity_at", handler.Last.RequestUri!.Query);
        Assert.Contains("per_page=100", handler.Last.RequestUri!.Query);
        Assert.Equal("gitlab.com", handler.Last.RequestUri!.Host);
        AssertTokenOnlyInAuthHeader(handler, repos.SelectMany(r => new[] { r.CloneUrl, r.FullName, r.Description ?? "" }));
    }

    [Fact]
    public async Task GitLab_SelfHosted_QueriesItsOwnOrigin()
    {
        using var tk = new TempKeyring();
        tk.Keyring.SaveSecret(GitHostDetector.TokenKeyForHost("gitlab.example.com"), Token);

        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        var svc = new HostRepositoryService(tk.Keyring, new HttpClient(handler));

        await svc.ListMyRepositoriesAsync("gitlab.example.com", HostKind.GitLab, CancellationToken.None);
        Assert.Equal("gitlab.example.com", handler.Last.RequestUri!.Host);
        Assert.Contains("/api/v4/projects", handler.Last.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task GitLab_UnauthorizedBody_MapsToTypedError_TokenScrubbed()
    {
        using var tk = new TempKeyring();
        tk.Keyring.SaveSecret(GitHostDetector.TokenKeyForHost("gitlab.com"), Token);

        var handler = new StubHandler(HttpStatusCode.Unauthorized, $$"""{"message":"401 Unauthorized for {{Token}}"}""");
        var svc = new HostRepositoryService(tk.Keyring, new HttpClient(handler));

        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => svc.ListMyRepositoriesAsync("gitlab.com", HostKind.GitLab, CancellationToken.None));
        Assert.DoesNotContain(Token, ex.Message);
    }

    // ---- Unsupported hosts & token gating --------------------------------------------------------

    [Theory]
    [InlineData("bitbucket.org", HostKind.Bitbucket)]
    [InlineData("dev.azure.com", HostKind.AzureDevOps)]
    public async Task UnimplementedHost_IsUnsupported_AndThrows(string host, HostKind kind)
    {
        using var tk = new TempKeyring();
        tk.Keyring.SaveSecret(GitHostDetector.TokenKeyForHost(host), Token); // even with a token stored

        var svc = new HostRepositoryService(tk.Keyring, new HttpClient(new StubHandler(HttpStatusCode.OK, "[]")));

        Assert.False(svc.IsSupported(host, kind));
        await Assert.ThrowsAsync<GitOperationException>(
            () => svc.ListMyRepositoriesAsync(host, kind, CancellationToken.None));
    }

    [Fact]
    public async Task NoStoredToken_IsUnsupported_AndThrowsAuthRequired()
    {
        using var tk = new TempKeyring();
        var svc = new HostRepositoryService(tk.Keyring, new HttpClient(new StubHandler(HttpStatusCode.OK, "[]")));

        Assert.False(svc.IsSupported("github.com", HostKind.GitHub));
        Assert.False(svc.IsSupported("gitlab.com", HostKind.GitLab));
        await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => svc.ListMyRepositoriesAsync("gitlab.com", HostKind.GitLab, CancellationToken.None));
    }
}
