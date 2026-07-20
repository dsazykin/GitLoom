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
using Mainguard.Git.Models;
using Mainguard.Git.Notifications;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-27 (provider parsing) — drives <see cref="GitHubNotificationProvider"/> against checked-in JSON
/// fixtures through an injected <see cref="HttpMessageHandler"/> (no live network). Asserts the mixed
/// read/unread list maps to the host-agnostic models (reason + subject-kind enums, best-effort api→html
/// URL), the unread-only vs all query, the mark-read (PATCH) and mark-all (PUT) request shapes, error
/// bodies map to typed exceptions, and — critically (G-4) — the token appears ONLY in the Authorization
/// header, never in a produced model string, a request URL/body, or an exception message.
/// </summary>
public class NotificationProviderTests
{
    private const string Token = "ghp_NOTIF_SeNtInEl_TOKEN_do_not_leak_555";

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

    private static GitHubNotificationProvider ProviderFor(HttpMessageHandler handler)
        => new(new HttpClient(handler));

    private static string Fixture(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var path = Path.Combine(dir ?? AppContext.BaseDirectory, "Mainguard.Tests", "Fixtures", "Notifications", name);
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

    // ---- List (mixed read/unread, all reasons/subjects) ---------------------------------------

    [Fact]
    public async Task ListAsync_ParsesMixedList_MapsReasonsSubjectsAndUrls()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_notifications_list.json"));
        var items = await ProviderFor(handler).ListAsync(Token, onlyUnread: false, CancellationToken.None);

        Assert.Equal(5, items.Count);

        var pr = items.First(i => i.Id == "3001");
        Assert.Equal(NotificationReason.ReviewRequested, pr.Reason);
        Assert.Equal(NotificationSubjectKind.PullRequest, pr.Kind);
        Assert.Equal("Add notifications inbox (T-27)", pr.Title);
        Assert.Equal("octocat/hello-world", pr.RepoFullName);
        Assert.True(pr.Unread);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 9, 30, 0, TimeSpan.Zero), pr.UpdatedAt);
        // api→html best-effort: /repos dropped, api host swapped, /pulls/ → /pull/.
        Assert.Equal("https://github.com/octocat/hello-world/pull/512", pr.Url);

        var issue = items.First(i => i.Id == "3002");
        Assert.Equal(NotificationReason.Mention, issue.Reason);
        Assert.Equal(NotificationSubjectKind.Issue, issue.Kind);
        Assert.Equal("https://github.com/octocat/hello-world/issues/101", issue.Url);

        var commit = items.First(i => i.Id == "3003");
        Assert.Equal(NotificationReason.CiActivity, commit.Reason);
        Assert.Equal(NotificationSubjectKind.Commit, commit.Kind);
        Assert.False(commit.Unread); // a read thread survives when onlyUnread=false
        Assert.Equal("https://github.com/octocat/hello-world/commit/9b3ea4bcafe1234567890", commit.Url);

        var release = items.First(i => i.Id == "3004");
        Assert.Equal(NotificationSubjectKind.Release, release.Kind);
        Assert.Equal("danielsazykin/mainguard", release.RepoFullName);

        // A subject with a null url yields an empty (no jump-to) URL, never a throw.
        var discussion = items.First(i => i.Id == "3005");
        Assert.Equal(NotificationSubjectKind.Discussion, discussion.Kind);
        Assert.Equal(NotificationReason.TeamMention, discussion.Reason);
        Assert.Equal("", discussion.Url);

        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task ListAsync_UnreadOnly_SetsAllFalse_AndParsesUnread()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_notifications_unread.json"));
        var items = await ProviderFor(handler).ListAsync(Token, onlyUnread: true, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.Unread));

        var uri = handler.Last.RequestUri!.ToString();
        Assert.Contains("/notifications", uri);
        Assert.Contains("all=false", uri);
        Assert.Equal(HttpMethod.Get, handler.Last.Method);
    }

    [Fact]
    public async Task ListAsync_All_SetsAllTrue()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "[]");
        await ProviderFor(handler).ListAsync(Token, onlyUnread: false, CancellationToken.None);

        Assert.Contains("all=true", handler.Last.RequestUri!.ToString());
    }

    // ---- Mark read / mark all -----------------------------------------------------------------

    [Fact]
    public async Task MarkReadAsync_PatchesThreadById()
    {
        var handler = new StubHandler(HttpStatusCode.ResetContent, "");
        await ProviderFor(handler).MarkReadAsync(Token, "3002", CancellationToken.None);

        Assert.Equal("PATCH", handler.Last.Method.Method);
        Assert.Contains("/notifications/threads/3002", handler.Last.RequestUri!.ToString());
        AssertTokenOnlyInAuthHeader(handler);
    }

    [Fact]
    public async Task MarkAllReadAsync_PutsNotifications_WithReadBody()
    {
        var handler = new StubHandler(HttpStatusCode.ResetContent, "");
        await ProviderFor(handler).MarkAllReadAsync(Token, CancellationToken.None);

        Assert.Equal(HttpMethod.Put, handler.Last.Method);
        Assert.EndsWith("/notifications", handler.Last.RequestUri!.AbsolutePath);
        Assert.Contains("\"read\":true", handler.LastBody);
        AssertTokenOnlyInAuthHeader(handler);
    }

    // ---- Errors -> typed ----------------------------------------------------------------------

    [Fact]
    public async Task Unauthorized_ThrowsAuthenticationRequired_WithHost()
    {
        var handler = new StubHandler(HttpStatusCode.Unauthorized, "{\"message\":\"Bad credentials\"}");
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).ListAsync(Token, onlyUnread: true, CancellationToken.None));

        Assert.Equal("github.com", ex.Host);
        Assert.Contains("Bad credentials", ex.Message);
    }

    [Fact]
    public async Task RateLimited_ThrowsTypedGitOperation()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "{\"message\":\"API rate limit exceeded\"}");
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).ListAsync(Token, onlyUnread: true, CancellationToken.None));

        Assert.Contains("rate limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkFailure_ThrowsTypedGitOperation_NotRaw()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Name or service not known"));
        var ex = await Assert.ThrowsAsync<GitOperationException>(() =>
            ProviderFor(handler).ListAsync(Token, onlyUnread: true, CancellationToken.None));

        Assert.Contains("Could not reach GitHub", ex.Message);
    }

    // ---- Token never leaks (G-4) --------------------------------------------------------------

    [Fact]
    public async Task Token_NeverAppearsInAnyProducedString()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Fixture("github_notifications_list.json"));
        var items = await ProviderFor(handler).ListAsync(Token, onlyUnread: false, CancellationToken.None);
        foreach (var i in items)
            Assert.DoesNotContain(Token, $"{i.Id}{i.Title}{i.RepoFullName}{i.Url}{i.Reason}{i.Kind}");
    }

    [Fact]
    public async Task ErrorMessage_EchoingToken_IsRedacted()
    {
        var body = "{\"message\":\"Bad token " + Token + " supplied\"}";
        var handler = new StubHandler(HttpStatusCode.Unauthorized, body);
        var ex = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            ProviderFor(handler).ListAsync(Token, onlyUnread: true, CancellationToken.None));

        Assert.DoesNotContain(Token, ex.Message);
        Assert.Contains("***", ex.Message);
    }
}
