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
using GitLoom.Core.Releases;
using GitLoom.Core.Services; // shared RepoSlug
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-28 (provider parsing) — drives <see cref="GitHubReleaseProvider"/> against checked-in JSON fixtures
/// through an injected <see cref="HttpMessageHandler"/> (no live network). Asserts the releases list maps to
/// the host-agnostic models (incl. draft/prerelease/name-fallback/null-published), the create request body
/// shape (tag/target/name/body/draft/prerelease), error bodies map to typed exceptions, and — critically
/// (G-4) — the token appears ONLY in the Authorization header, never in a URL/body/model/exception.
/// </summary>
public class ReleaseProviderTests
{
    private const string Token = "ghp_RELEASE_SeNtInEl_TOKEN_do_not_leak_555";
    private static readonly RepoSlug Slug = new("octocat", "hello-world");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _responder;
        public readonly List<HttpRequestMessage> Requests = new();
        public readonly List<string> RequestBodies = new();

        public StubHandler(HttpStatusCode status, string body) : this(_ => (status, body)) { }
        public StubHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            RequestBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            var (status, body) = _responder(request);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        public HttpRequestMessage Last => Requests[^1];
        public string LastBody => RequestBodies[^1];
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw _ex;
    }

    private static GitHubReleaseProvider ProviderFor(HttpMessageHandler handler)
        => new(new HttpClient(handler));

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, "GitLoom.Tests", "Fixtures", "Releases", name);
        return File.ReadAllText(path);
    }

    // ---- List ----------------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_MapsReleases_WithDraftAndPrerelease()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("list.json"));
        var items = await ProviderFor(handler).ListAsync(Slug, Token, CancellationToken.None);

        Assert.Equal(3, items.Count);

        var stable = items[0];
        Assert.Equal("v2.0.0", stable.TagName);
        Assert.Equal("GitLoom 2.0", stable.Name);
        Assert.False(stable.IsDraft);
        Assert.False(stable.IsPrerelease);
        Assert.Equal("octocat", stable.Author);
        Assert.NotNull(stable.PublishedAt);

        Assert.True(items[1].IsPrerelease);

        var draft = items[2];
        Assert.True(draft.IsDraft);
        Assert.Null(draft.PublishedAt);
        Assert.Equal("v2.2.0", draft.Name); // empty name falls back to the tag
    }

    [Fact]
    public async Task ListAsync_HitsReleasesEndpoint_WithBearer_NoTokenInUrl()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        await ProviderFor(handler).ListAsync(Slug, Token, CancellationToken.None);

        Assert.Contains("/repos/octocat/hello-world/releases", handler.Last.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Last.Headers.Authorization?.Scheme);
        Assert.Equal(Token, handler.Last.Headers.Authorization?.Parameter);
        Assert.DoesNotContain(Token, handler.Last.RequestUri!.ToString());
    }

    // ---- Create --------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_SendsExpectedBody_AndMapsResult()
    {
        var handler = new StubHandler(HttpStatusCode.Created, Fixture("created.json"));
        var request = new CreateRelease
        {
            TagName = "v3.0.0",
            TargetCommitish = "main",
            Name = "GitLoom 3.0",
            Body = "notes here",
            IsDraft = true,
            IsPrerelease = false,
        };

        var created = await ProviderFor(handler).CreateAsync(Slug, Token, request, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Last.Method);
        Assert.Contains("/repos/octocat/hello-world/releases", handler.Last.RequestUri!.ToString());
        var body = handler.LastBody;
        Assert.Contains("\"tag_name\":\"v3.0.0\"", body);
        Assert.Contains("\"target_commitish\":\"main\"", body);
        Assert.Contains("\"name\":\"GitLoom 3.0\"", body);
        Assert.Contains("\"body\":\"notes here\"", body);
        Assert.Contains("\"draft\":true", body);
        Assert.Contains("\"prerelease\":false", body);

        Assert.Equal("v3.0.0", created.TagName);
        Assert.True(created.IsDraft);
    }

    [Fact]
    public async Task CreateAsync_OmitsTargetCommitish_WhenBlank()
    {
        var handler = new StubHandler(HttpStatusCode.Created, Fixture("created.json"));
        var request = new CreateRelease { TagName = "v3.0.0", TargetCommitish = "", Name = "x", Body = "y" };

        await ProviderFor(handler).CreateAsync(Slug, Token, request, CancellationToken.None);

        Assert.DoesNotContain("target_commitish", handler.LastBody);
    }

    // ---- Errors --------------------------------------------------------------------------------

    [Fact]
    public async Task Create_422AlreadyExists_MapsToTyped_WithHostMessage()
    {
        var handler = new StubHandler(HttpStatusCode.UnprocessableEntity, Fixture("error_422.json"));
        var request = new CreateRelease { TagName = "v2.0.0", TargetCommitish = "main" };

        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).CreateAsync(Slug, Token, request, CancellationToken.None));
        Assert.Contains("Validation Failed", ex.Message);
    }

    [Fact]
    public async Task List_401_MapsToAuthenticationRequired_WithHost()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}");
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, CancellationToken.None));
        Assert.Equal("github.com", ex.Host);
    }

    [Fact]
    public async Task List_NetworkDown_MapsToTyped_NotRaw()
    {
        var handler = new ThrowingHandler(new HttpRequestException("socket boom"));
        await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, CancellationToken.None));
    }

    // ---- Token security (G-4) ------------------------------------------------------------------

    [Fact]
    public async Task Token_NeverLeaks_IntoUrlBodyModelOrException()
    {
        // A success round-trip: token must not appear in the URL, request body, or any produced model string.
        var listHandler = new StubHandler(HttpStatusCode.OK, Fixture("list.json"));
        var items = await ProviderFor(listHandler).ListAsync(Slug, Token, CancellationToken.None);

        Assert.DoesNotContain(Token, listHandler.Last.RequestUri!.ToString());
        Assert.DoesNotContain(Token, listHandler.LastBody);
        foreach (var it in items)
            Assert.DoesNotContain(Token, $"{it.TagName}|{it.Name}|{it.Body}|{it.Author}|{it.Url}");

        // An error body echoing the token must be redacted out of the thrown message.
        var leaky = new StubHandler(HttpStatusCode.Forbidden, $"{{\"message\":\"token {Token} is forbidden\"}}");
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(leaky).ListAsync(Slug, Token, CancellationToken.None));
        Assert.DoesNotContain(Token, ex.Message);
    }
}
