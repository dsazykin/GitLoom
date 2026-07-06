using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Checks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Services; // shared RepoSlug
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-26 (provider parsing) — drives <see cref="GitHubCheckProvider"/> against checked-in JSON fixtures
/// through an injected <see cref="HttpMessageHandler"/> (no live network). Asserts that check-runs and the
/// legacy combined status are parsed and <b>merged</b> (dedup by name, check-run wins), that mapping/roll-up
/// match the pinned rules, that re-run posts to the right endpoint, that errors map to typed exceptions,
/// and — critically (G-4) — the token appears ONLY in the Authorization header, never a URL/body/model
/// string/exception message.
/// </summary>
public class CheckProviderTests
{
    private const string Token = "ghp_CHECKS_SeNtInEl_TOKEN_do_not_leak_654";
    private static readonly RepoSlug Slug = new("octocat", "hello-world");
    private const string Sha = "9b3ea4bfeedfaced00000000000000000000cafe";

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
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _ex;
        public ThrowingHandler(Exception ex) => _ex = ex;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw _ex;
    }

    private static GitHubCheckProvider ProviderFor(HttpMessageHandler handler) => new(new HttpClient(handler));

    // Routes the two GET surfaces to the right fixtures for a merged GetChecksAsync.
    private static StubHandler MergedHandler(string checkRunsFixture, string statusFixture) => new(req =>
        req.RequestUri!.AbsolutePath.EndsWith("/check-runs", StringComparison.Ordinal)
            ? (HttpStatusCode.OK, Fixture(checkRunsFixture))
            : (HttpStatusCode.OK, Fixture(statusFixture)));

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        return File.ReadAllText(Path.Combine(dir ?? AppContext.BaseDirectory, "GitLoom.Tests", "Fixtures", "Checks", name));
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

    // ---- Merge + parse + roll-up --------------------------------------------------------------

    [Fact]
    public async Task GetChecksAsync_MergesRunsAndLegacyStatus_DedupByName_CheckRunWins()
    {
        var handler = MergedHandler("github_check_runs.json", "github_commit_status.json");
        var checks = await ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None);

        // 4 check-runs + 1 NEW legacy status (deploy-preview); the legacy "build (ubuntu)" is dropped (dup name).
        Assert.Equal(5, checks.Runs.Count);
        Assert.Single(checks.Runs, r => r.Name == "build (ubuntu)");

        var build = checks.Runs.First(r => r.Name == "build (ubuntu)");
        Assert.Equal(CheckState.Success, build.State);   // check-run success wins over the legacy failure
        Assert.Equal(8001, build.Id);
        Assert.True(build.CanRerun);

        var deploy = checks.Runs.First(r => r.Name == "deploy-preview");
        Assert.Equal(CheckState.Success, deploy.State);
        Assert.Equal(0, deploy.Id);                      // legacy status has no re-requestable id
        Assert.False(deploy.CanRerun);
        Assert.Equal("https://deploy.example.com/preview/123", deploy.DetailsUrl);

        // Roll-up: a failing test dominates. Passed = build + deploy; Failed = test; Pending = lint; coverage neutral.
        Assert.Equal(CheckState.Failure, checks.Overall);
        Assert.Equal(2, checks.Passed);
        Assert.Equal(1, checks.Failed);
        Assert.Equal(1, checks.Pending);
        Assert.True(checks.HasAny);
        Assert.Equal(Sha, checks.Sha);

        var lint = checks.Runs.First(r => r.Name == "lint");
        Assert.Equal(CheckState.Pending, lint.State);
        Assert.Equal("https://github.com/octocat/hello-world/runs/8003", lint.DetailsUrl);
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task GetChecksAsync_TargetsCommitScopedEndpoints()
    {
        var handler = MergedHandler("github_check_runs.json", "github_commit_status.json");
        await ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None);

        Assert.Contains(handler.Requests, r => r.RequestUri!.ToString()
            .Contains($"/repos/octocat/hello-world/commits/{Sha}/check-runs"));
        Assert.Contains(handler.Requests, r => r.RequestUri!.ToString()
            .Contains($"/repos/octocat/hello-world/commits/{Sha}/status"));
        Assert.All(handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task GetChecksAsync_NoChecks_HasAnyFalse_NoThrow()
    {
        var handler = MergedHandler("github_check_runs_empty.json", "github_commit_status_empty.json");
        var checks = await ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None);

        Assert.False(checks.HasAny);
        Assert.Empty(checks.Runs);
        Assert.Equal(0, checks.Passed);
    }

    [Fact]
    public async Task GetChecksAsync_LegacyOnly_ParsesStatuses()
    {
        var handler = MergedHandler("github_check_runs_empty.json", "github_commit_status.json");
        var checks = await ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None);

        Assert.Equal(2, checks.Runs.Count); // build (ubuntu) failure + deploy-preview success
        Assert.Equal(CheckState.Failure, checks.Overall);
        Assert.All(checks.Runs, r => Assert.Equal(0, r.Id));
    }

    // ---- Re-run -------------------------------------------------------------------------------

    [Fact]
    public async Task RerequestAsync_PostsToRerequestEndpoint()
    {
        var handler = new StubHandler(HttpStatusCode.Created, "{}");
        await ProviderFor(handler).RerequestAsync(Slug, Token, 8002, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Last.Method);
        Assert.Contains("/repos/octocat/hello-world/check-runs/8002/rerequest", handler.Last.RequestUri!.ToString());
        AssertTokenOnlyInAuthHeader(handler);
    }

    // ---- Errors -> typed ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthorized_ThrowsAuthenticationRequired_WithHost()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, Fixture("github_error_401.json"));
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None));

        Assert.Equal("github.com", ex.Host);
        Assert.Contains("Bad credentials", ex.Message);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsTypedGitOperation_NotRaw()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Name or service not known"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None));

        Assert.Contains("Could not reach GitHub", ex.Message);
    }

    // ---- Token never leaks (G-4) --------------------------------------------------------------

    [Fact]
    public async Task Token_NeverAppearsInAnyProducedString()
    {
        var handler = MergedHandler("github_check_runs.json", "github_commit_status.json");
        var checks = await ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None);

        foreach (var r in checks.Runs)
            Assert.DoesNotContain(Token, $"{r.Id}{r.Name}{r.State}{r.RawStatus}{r.Conclusion}{r.DetailsUrl}");
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task ErrorMessage_EchoingToken_IsRedacted()
    {
        var body = "{\"message\":\"Bad token " + Token + " supplied\"}";
        var handler = new StubHandler(HttpStatusCode.Unauthorized, body);
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).GetChecksAsync(Slug, Token, Sha, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("***", ex.Message);
    }

    // ---- Stub providers report unsupported ----------------------------------------------------

    [Fact]
    public async Task StubProvider_IsNotImplemented_AndThrowsTyped()
    {
        ICheckProvider stub = new GitLabCheckProvider();
        Assert.False(stub.IsImplemented);
        await Assert.ThrowsAsync<GitOperationException>(() =>
            stub.GetChecksAsync(Slug, Token, Sha, CancellationToken.None));
    }
}
