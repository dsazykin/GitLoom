using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Commits;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-32 (provider parsing) — drives <see cref="GitHubCommitContextProvider"/> against checked-in JSON
/// fixtures through an injected <see cref="HttpMessageHandler"/> (no live network). Asserts the
/// commit→pulls endpoint maps to host-agnostic <see cref="PullRequestItem"/>s (one / several / none),
/// linked issues are parsed from the PR bodies+titles (bare <c>#n</c> → the commit's repo; cross-repo
/// kept; deduped), error bodies map to typed exceptions, and — critically (G-4) — the token appears ONLY
/// in the Authorization header, never in a produced model string, a request URL, or an exception message.
/// </summary>
public class CommitContextProviderTests
{
    private const string Token = "ghp_SeNtInEl_TOKEN_do_not_leak_ctx_123";
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private static readonly RepoSlug Slug = new("octocat", "hello-world");

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, (HttpStatusCode Status, string Body)> _responder;
        public readonly List<HttpRequestMessage> Requests = new();

        public StubHandler(HttpStatusCode status, string body) => _responder = _ => (status, body);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var (status, body) = _responder(request);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }

        public HttpRequestMessage Last => Requests[^1];
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw _ex;
    }

    private static GitHubCommitContextProvider ProviderFor(HttpMessageHandler handler)
        => new(new HttpClient(handler));

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return File.ReadAllText(Path.Combine(dir ?? AppContext.BaseDirectory, "GitLoom.Tests", "Fixtures", "CommitContext", name));
    }

    private static void AssertTokenOnlyInAuthHeader(StubHandler handler)
    {
        foreach (var req in handler.Requests)
        {
            Assert.Equal("Bearer", req.Headers.Authorization?.Scheme);
            Assert.Equal(Token, req.Headers.Authorization?.Parameter);
            Assert.DoesNotContain(Token, req.RequestUri!.ToString());
        }
    }

    // ---- Commit → pulls -----------------------------------------------------------------------

    [Fact]
    public async Task GetForCommit_TargetsCommitPullsEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        await ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None);

        var uri = handler.Last.RequestUri!.ToString();
        Assert.Contains($"/repos/octocat/hello-world/commits/{Sha}/pulls", uri);
        Assert.Equal(HttpMethod.Get, handler.Last.Method);
    }

    [Fact]
    public async Task GetForCommit_OnePr_MapsPr_AndLinkedIssues()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("commit_pulls_one.json"));
        var result = await ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None);

        Assert.Equal(Sha, result.Sha);
        var pr = Assert.Single(result.PullRequests);
        Assert.Equal(42, pr.Number);
        Assert.Equal(PullRequestState.Merged, pr.State); // merged_at set
        Assert.Equal("https://github.com/octocat/hello-world/pull/42", pr.Url);

        // Title "fixes #7", body "Closes #12 and resolves #12" (dedup), and cross-repo octocat/spec#3.
        Assert.Contains(result.LinkedIssues, i => i.Number == 7 && i.RepoFullName == "octocat/hello-world");
        Assert.Contains(result.LinkedIssues, i => i.Number == 12 && i.RepoFullName == "octocat/hello-world");
        Assert.Contains(result.LinkedIssues, i => i.Number == 3 && i.RepoFullName == "octocat/spec");
        Assert.Equal(3, result.LinkedIssues.Count); // #7, #12 (deduped), octocat/spec#3
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task GetForCommit_SeveralPrs_AllReturned_IssuesDedupedAcrossPrs()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("commit_pulls_several.json"));
        var result = await ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None);

        Assert.Equal(2, result.PullRequests.Count);
        Assert.Contains(result.PullRequests, p => p.Number == 42);
        Assert.Contains(result.PullRequests, p => p.Number == 55 && p.State == PullRequestState.Draft);

        // Both PRs mention #7; the backport also mentions #42. Deduped: #7 and #42.
        Assert.Equal(2, result.LinkedIssues.Count);
        Assert.Contains(result.LinkedIssues, i => i.Number == 7);
        Assert.Contains(result.LinkedIssues, i => i.Number == 42);
    }

    [Fact]
    public async Task GetForCommit_NoPr_EmptyResult_NoThrow()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("commit_pulls_none.json"));
        var result = await ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None);

        Assert.Empty(result.PullRequests);
        Assert.Empty(result.LinkedIssues);
        Assert.Equal(Sha, result.Sha);
    }

    // ---- Errors -> typed ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthorized_ThrowsAuthenticationRequired()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}");
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None));
        Assert.Equal("github.com", ex.Host);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsTypedGitOperation_NotRaw()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Name or service not known"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None));
        Assert.Contains("Could not reach GitHub", ex.Message);
    }

    // ---- Token never leaks (G-4) --------------------------------------------------------------

    [Fact]
    public async Task Token_NeverAppearsInAnyProducedString()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("commit_pulls_one.json"));
        var result = await ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None);

        foreach (var pr in result.PullRequests)
            Assert.DoesNotContain(Token, $"{pr.Number}{pr.Title}{pr.Author}{pr.SourceBranch}{pr.TargetBranch}{pr.Url}{pr.State}");
        foreach (var i in result.LinkedIssues)
            Assert.DoesNotContain(Token, $"{i.Number}{i.RepoFullName}");
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task ErrorMessage_EchoingToken_IsRedacted()
    {
        var body = "{\"message\":\"Bad token " + Token + " supplied\"}";
        var handler = new StubHandler(HttpStatusCode.Unauthorized, body);
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).GetForCommitAsync(Slug, Token, Sha, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("***", ex.Message);
    }

    // ---- Stubs report unsupported --------------------------------------------------------------

    [Fact]
    public async Task StubProviders_AreNotImplemented_AndThrowTyped()
    {
        ICommitContextProvider[] stubs =
        {
            new GitLabCommitContextProvider(),
            new BitbucketCommitContextProvider(),
            new AzureDevOpsCommitContextProvider(),
        };
        foreach (var stub in stubs)
        {
            Assert.False(stub.IsImplemented);
            await Assert.ThrowsAsync<GitOperationException>(() =>
                stub.GetForCommitAsync(Slug, Token, Sha, CancellationToken.None));
        }
    }
}
