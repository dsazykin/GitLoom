using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Issues;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-24 (provider parsing) — drives <see cref="GitHubIssueProvider"/> against checked-in JSON fixtures
/// through an injected <see cref="HttpMessageHandler"/> (no live network). Asserts list / get+comments /
/// create / comment / set-state map to the host-agnostic models, the <b>PR-vs-issue</b> filter drops
/// pull requests returned by the issues endpoint, error bodies map to the right typed exceptions, and —
/// critically (G-4) — the token appears ONLY in the Authorization header, never in a produced model
/// string, a request URL/body, or an exception message.
/// </summary>
public class IssueProviderTests
{
    private const string Token = "ghp_ISSUE_SeNtInEl_TOKEN_do_not_leak_987";
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

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw _ex;
    }

    private static GitHubIssueProvider ProviderFor(HttpMessageHandler handler)
        => new(new HttpClient(handler));

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, "Mainguard.Tests", "Fixtures", "Issues", name);
        return File.ReadAllText(path);
    }

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

    // ---- List (incl. the critical PR filter) --------------------------------------------------

    [Fact]
    public async Task ListAsync_FiltersOutPullRequests_AndParsesIssues()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_issues_list.json"));
        var items = await ProviderFor(handler).ListAsync(Slug, Token, IssueState.Open, CancellationToken.None);

        // The fixture has 3 rows; #99 carries a pull_request object and MUST be excluded.
        Assert.Equal(2, items.Count);
        Assert.DoesNotContain(items, i => i.Number == 99);
        Assert.All(items, i => Assert.NotEqual(99, i.Number));

        var bug = items.First(i => i.Number == 101);
        Assert.Equal("Crash on startup when repo name contains an emoji 🚀", bug.Title);
        Assert.Equal("danielsazykin", bug.Author);
        Assert.Equal(IssueState.Open, bug.State);
        Assert.Equal(3, bug.CommentCount);
        Assert.Equal(new[] { "bug", "priority: high" }, bug.Labels.Select(l => l.Name).ToArray());
        Assert.Equal("d73a4a", bug.Labels[0].Color);
        Assert.Equal(new[] { "octocat", "hubot" }, bug.Assignees.ToArray());
        Assert.Equal("https://github.com/octocat/hello-world/issues/101", bug.Url);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero), bug.UpdatedAt);

        var plain = items.First(i => i.Number == 100);
        Assert.Empty(plain.Assignees);
        Assert.Single(plain.Labels);
        Assert.Equal(0, plain.CommentCount);

        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task ListAsync_TargetsCorrectOwnerRepoPath_AndState()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        await ProviderFor(handler).ListAsync(Slug, Token, IssueState.Closed, CancellationToken.None);

        var uri = handler.Last.RequestUri!.ToString();
        Assert.Contains("/repos/octocat/hello-world/issues", uri);
        Assert.Contains("state=closed", uri);
        Assert.Equal(HttpMethod.Get, handler.Last.Method);
    }

    // ---- Get + comments -----------------------------------------------------------------------

    [Fact]
    public async Task GetAsync_ParsesDetail_AndComments()
    {
        var handler = new StubHandler(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal)
                ? (HttpStatusCode.OK, Fixture("github_issue_comments.json"))
                : (HttpStatusCode.OK, Fixture("github_issue_detail.json")));

        var detail = await ProviderFor(handler).GetAsync(Slug, Token, 101, CancellationToken.None);

        Assert.Equal(101, detail.Summary.Number);
        Assert.Contains("multi-byte unicode", detail.Body);
        Assert.Contains("日本語", detail.Body);
        Assert.Equal(2, detail.Comments.Count);
        Assert.Equal("octocat", detail.Comments[0].Author);
        Assert.Contains("reproduce", detail.Comments[0].Body);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 11, 0, 0, TimeSpan.Zero), detail.Comments[0].When);
        AssertTokenOnlyInAuthHeader(handler);
    }

    // ---- Create -------------------------------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PostsExpectedBody_AndParsesCreated()
    {
        var handler = new StubHandler(HttpStatusCode.Created, Fixture("github_issue_created.json"));
        var created = await ProviderFor(handler).CreateAsync(Slug, Token, new CreateIssue
        {
            Title = "Support GitLab issues too",
            Body = "Extend the provider to GitLab's /issues endpoint.",
            Labels = new[] { "enhancement" },
            Assignees = new[] { "danielsazykin" },
        }, CancellationToken.None);

        Assert.Equal(102, created.Number);
        Assert.Equal("Support GitLab issues too", created.Title);

        Assert.Equal(HttpMethod.Post, handler.Last.Method);
        Assert.Contains("/repos/octocat/hello-world/issues", handler.Last.RequestUri!.ToString());
        Assert.Contains("\"title\":\"Support GitLab issues too\"", handler.LastBody);
        Assert.Contains("\"labels\":[\"enhancement\"]", handler.LastBody);
        Assert.Contains("\"assignees\":[\"danielsazykin\"]", handler.LastBody);
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task CreateAsync_WithInvalidLabel_Surfaces422HostMessage_Typed()
    {
        var handler = new StubHandler((HttpStatusCode)422, Fixture("github_error_422_labels.json"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).CreateAsync(Slug, Token, new CreateIssue { Title = "x", Labels = new[] { "nope" } }, CancellationToken.None));

        Assert.Contains("could not add label", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("422", ex.Message);
    }

    // ---- Comment ------------------------------------------------------------------------------

    [Fact]
    public async Task CommentAsync_PostsBody_AndParsesComment()
    {
        var handler = new StubHandler(HttpStatusCode.Created, Fixture("github_issue_comment_created.json"));
        var comment = await ProviderFor(handler).CommentAsync(Slug, Token, 101, "Thanks for the report — reproduced and fixing now.", CancellationToken.None);

        Assert.Equal("danielsazykin", comment.Author);
        Assert.Contains("reproduced", comment.Body);
        Assert.Equal(HttpMethod.Post, handler.Last.Method);
        Assert.Contains("/repos/octocat/hello-world/issues/101/comments", handler.Last.RequestUri!.ToString());
        Assert.Contains("\"body\":", handler.LastBody);
        AssertTokenOnlyInAuthHeader(handler);
    }

    // ---- Close / Reopen -----------------------------------------------------------------------

    [Fact]
    public async Task SetStateAsync_Close_PatchesStateClosed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_issue_closed.json"));
        var item = await ProviderFor(handler).SetStateAsync(Slug, Token, 101, IssueState.Closed, CancellationToken.None);

        Assert.Equal(IssueState.Closed, item.State);
        Assert.Equal("PATCH", handler.Last.Method.Method);
        Assert.Contains("/repos/octocat/hello-world/issues/101", handler.Last.RequestUri!.ToString());
        Assert.Contains("\"state\":\"closed\"", handler.LastBody);
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task SetStateAsync_Reopen_PatchesStateOpen()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_issue_detail.json"));
        var item = await ProviderFor(handler).SetStateAsync(Slug, Token, 101, IssueState.Open, CancellationToken.None);

        Assert.Equal(IssueState.Open, item.State);
        Assert.Contains("\"state\":\"open\"", handler.LastBody);
    }

    // ---- Errors -> typed ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthorized_ThrowsAuthenticationRequired_WithHost()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, Fixture("github_error_401.json"));
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, IssueState.Open, CancellationToken.None));

        Assert.Equal("github.com", ex.Host);
        Assert.Contains("Bad credentials", ex.Message);
    }

    [Fact]
    public async Task RateLimited_ThrowsTypedGitOperation()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, Fixture("github_error_403_ratelimit.json"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, IssueState.Open, CancellationToken.None));

        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsTypedGitOperation_NotRaw()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Name or service not known"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, IssueState.Open, CancellationToken.None));

        Assert.Contains("Could not reach GitHub", ex.Message);
    }

    // ---- Token never leaks (G-4) --------------------------------------------------------------

    [Fact]
    public async Task Token_NeverAppearsInAnyProducedString_AcrossOperations()
    {
        // List
        var listHandler = new StubHandler(HttpStatusCode.OK, Fixture("github_issues_list.json"));
        var items = await ProviderFor(listHandler).ListAsync(Slug, Token, IssueState.Open, CancellationToken.None);
        foreach (var i in items)
        {
            var labelText = string.Concat(i.Labels.Select(l => l.Name + l.Color));
            Assert.DoesNotContain(Token, $"{i.Number}{i.Title}{i.Author}{i.Url}{i.State}{labelText}{string.Concat(i.Assignees)}");
        }

        // Create
        var createHandler = new StubHandler(HttpStatusCode.Created, Fixture("github_issue_created.json"));
        var created = await ProviderFor(createHandler).CreateAsync(Slug, Token, new CreateIssue { Title = "t" }, CancellationToken.None);
        Assert.DoesNotContain(Token, $"{created.Title}{created.Url}");

        // Comment
        var commentHandler = new StubHandler(HttpStatusCode.Created, Fixture("github_issue_comment_created.json"));
        var comment = await ProviderFor(commentHandler).CommentAsync(Slug, Token, 1, "hi", CancellationToken.None);
        Assert.DoesNotContain(Token, $"{comment.Author}{comment.Body}");
    }

    [Fact]
    public async Task ErrorMessage_EchoingToken_IsRedacted()
    {
        var body = "{\"message\":\"Bad token " + Token + " supplied\"}";
        var handler = new StubHandler(HttpStatusCode.Unauthorized, body);
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).ListAsync(Slug, Token, IssueState.Open, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("***", ex.Message);
    }
}
