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
using GitLoom.Core.PullRequests;
using GitLoom.Core.Services; // shared RepoSlug
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-23 (provider parsing) — drives <see cref="GitHubPullRequestProvider"/> against checked-in JSON
/// fixtures through an injected <see cref="HttpMessageHandler"/> (no live network). Asserts list /
/// create / merge / close / get map to the host-agnostic models, error bodies map to the right typed
/// exceptions, and — critically (G-4) — the token appears ONLY in the Authorization header, never in a
/// produced model string, a request URL, or an exception message.
/// </summary>
public class PullRequestProviderTests
{
    private const string Token = "ghp_SeNtInEl_TOKEN_do_not_leak_123";
    private static readonly RepoSlug Slug = new("octocat", "hello-world");

    // ---- Fixture-response handler -------------------------------------------------------------

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

    private static GitHubPullRequestProvider ProviderFor(HttpMessageHandler handler)
        => new(new HttpClient(handler));

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, "GitLoom.Tests", "Fixtures", "PullRequests", name);
        return File.ReadAllText(path);
    }

    // Asserts the token is confined to the Authorization header on every request the handler saw.
    private static void AssertTokenOnlyInAuthHeader(StubHandler handler)
    {
        foreach (var req in handler.Requests)
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            Assert.DoesNotContain(Token, req.RequestUri!.ToString());
        }
        foreach (var body in handler.RequestBodies)
            Assert.DoesNotContain(Token, body);
    }

    // ---- List ---------------------------------------------------------------------------------

    [Fact]
    public async Task ListAsync_ParsesFixture_ToItems()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_pulls_list.json"));
        var items = await ProviderFor(handler).ListAsync(Slug, Token, PullRequestState.Open, CancellationToken.None);

        Assert.Equal(3, items.Count);
        var pr = items.First(i => i.Number == 42);
        Assert.Equal("Add multi-host PR integration", pr.Title);
        Assert.Equal("danielsazykin", pr.Author);
        Assert.Equal("feature/T-23-pr-integration", pr.SourceBranch);
        Assert.Equal("main", pr.TargetBranch);
        Assert.False(pr.IsDraft);
        Assert.Equal(PullRequestState.Open, pr.State);
        Assert.Equal("https://github.com/octocat/hello-world/pull/42", pr.Url);

        var draft = items.First(i => i.Number == 41);
        Assert.True(draft.IsDraft);
        Assert.Equal(PullRequestState.Draft, draft.State);

        var merged = items.First(i => i.Number == 39);
        Assert.Equal(PullRequestState.Merged, merged.State); // merged_at set

        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task ListAsync_TargetsCorrectOwnerRepoPath()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        await ProviderFor(handler).ListAsync(Slug, Token, PullRequestState.Open, CancellationToken.None);

        var uri = handler.Last.RequestUri!.ToString();
        Assert.Contains("/repos/octocat/hello-world/pulls", uri);
        Assert.Contains("state=open", uri);
        Assert.Equal(HttpMethod.Get, handler.Last.Method);
    }

    [Fact]
    public async Task ListAsync_MergedFilter_KeepsOnlyMerged()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_pulls_list.json"));
        var items = await ProviderFor(handler).ListAsync(Slug, Token, PullRequestState.Merged, CancellationToken.None);

        Assert.Single(items);
        Assert.Equal(39, items[0].Number);
    }

    // ---- Get ----------------------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ParsesDetail_WithReviewersAndMergeable()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_pull_detail.json"));
        var detail = await ProviderFor(handler).GetAsync(Slug, Token, 42, CancellationToken.None);

        Assert.Equal(42, detail.Summary.Number);
        Assert.True(detail.Mergeable);
        Assert.Contains("octocat", detail.Reviewers);
        Assert.Contains("hubot", detail.Reviewers);
        Assert.Contains("v1 provider", detail.Body);
        AssertTokenOnlyInAuthHeader(handler);
    }

    // ---- Create -------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_ParsesCreatedPr_AndPostsExpectedBody()
    {
        var handler = new StubHandler(HttpStatusCode.Created, Fixture("github_pull_created.json"));
        var created = await ProviderFor(handler).CreateAsync(Slug, Token, new CreatePullRequest
        {
            Title = "Reflog viewer & recovery",
            Body = "body",
            SourceBranch = "feature/T-20-reflog-viewer",
            TargetBranch = "main",
            IsDraft = true,
        }, CancellationToken.None);

        Assert.Equal(43, created.Number);
        Assert.Equal("Reflog viewer & recovery", created.Title);
        Assert.True(created.IsDraft);

        Assert.Equal(HttpMethod.Post, handler.Last.Method);
        Assert.Contains("\"head\":\"feature/T-20-reflog-viewer\"", handler.LastBody);
        Assert.Contains("\"base\":\"main\"", handler.LastBody);
        Assert.Contains("\"draft\":true", handler.LastBody);
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task CreateAsync_When422AlreadyExists_ThrowsTypedAlreadyExists()
    {
        var handler = new StubHandler((HttpStatusCode)422, Fixture("github_error_422_exists.json"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).CreateAsync(Slug, Token, new CreatePullRequest(), CancellationToken.None));

        Assert.Contains("already exists", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Merge --------------------------------------------------------------------------------

    [Fact]
    public async Task MergeAsync_WhenMerged_ReturnsMergedItem_AndMapsMethod()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_pull_merged.json"));
        var item = await ProviderFor(handler).MergeAsync(Slug, Token, 42, PullRequestMergeMethod.Squash, CancellationToken.None);

        Assert.Equal(42, item.Number);
        Assert.Equal(PullRequestState.Merged, item.State);
        Assert.Equal(HttpMethod.Put, handler.Last.Method);
        Assert.Contains("\"merge_method\":\"squash\"", handler.LastBody);
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task MergeAsync_WhenNotMergeable_ThrowsTyped()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_merge_not_mergeable.json"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).MergeAsync(Slug, Token, 42, PullRequestMergeMethod.Merge, CancellationToken.None));

        Assert.Contains("not mergeable", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MergeAsync_When405_ThrowsTyped()
    {
        var handler = new StubHandler((HttpStatusCode)405, "{\"message\":\"Pull Request is not mergeable\"}");
        await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).MergeAsync(Slug, Token, 42, PullRequestMergeMethod.Merge, CancellationToken.None));
    }

    // ---- Close --------------------------------------------------------------------------------

    [Fact]
    public async Task CloseAsync_PatchesStateClosed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_pull_created.json"));
        await ProviderFor(handler).CloseAsync(Slug, Token, 43, CancellationToken.None);

        Assert.Equal("PATCH", handler.Last.Method.Method);
        Assert.Contains("/repos/octocat/hello-world/pulls/43", handler.Last.RequestUri!.ToString());
        Assert.Contains("\"state\":\"closed\"", handler.LastBody);
        AssertTokenOnlyInAuthHeader(handler);
    }

    // ---- Errors -> typed ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthorized_ThrowsAuthenticationRequired_WithHost()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, Fixture("github_error_401.json"));
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, PullRequestState.Open, CancellationToken.None));

        Assert.Equal("github.com", ex.Host);
        Assert.Contains("Bad credentials", ex.Message);
    }

    [Fact]
    public async Task RateLimited_ThrowsTypedGitOperation()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, Fixture("github_error_403_ratelimit.json"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, PullRequestState.Open, CancellationToken.None));

        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsTypedGitOperation_NotRaw()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Name or service not known"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, PullRequestState.Open, CancellationToken.None));

        Assert.Contains("Could not reach GitHub", ex.Message);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw _ex;
    }

    // ---- Token never leaks (G-4) --------------------------------------------------------------

    [Fact]
    public async Task Token_NeverAppearsInAnyProducedString_AcrossOperations()
    {
        // List
        var listHandler = new StubHandler(HttpStatusCode.OK, Fixture("github_pulls_list.json"));
        var items = await ProviderFor(listHandler).ListAsync(Slug, Token, PullRequestState.Open, CancellationToken.None);
        foreach (var i in items)
            Assert.DoesNotContain(Token, $"{i.Number}{i.Title}{i.Author}{i.SourceBranch}{i.TargetBranch}{i.Url}{i.State}");

        // Create
        var createHandler = new StubHandler(HttpStatusCode.Created, Fixture("github_pull_created.json"));
        var created = await ProviderFor(createHandler).CreateAsync(Slug, Token, new CreatePullRequest(), CancellationToken.None);
        Assert.DoesNotContain(Token, $"{created.Title}{created.Url}");

        // Merge
        var mergeHandler = new StubHandler(HttpStatusCode.OK, Fixture("github_pull_merged.json"));
        var merged = await ProviderFor(mergeHandler).MergeAsync(Slug, Token, 42, PullRequestMergeMethod.Merge, CancellationToken.None);
        Assert.DoesNotContain(Token, $"{merged.Number}{merged.State}");
    }

    [Fact]
    public async Task ErrorMessage_EchoingToken_IsRedacted()
    {
        // A hostile/echoing host response that folds the token into its message must be scrubbed.
        var body = "{\"message\":\"Bad token " + Token + " supplied\"}";
        var handler = new StubHandler(HttpStatusCode.Unauthorized, body);
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, PullRequestState.Open, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("***", ex.Message);
    }
}
