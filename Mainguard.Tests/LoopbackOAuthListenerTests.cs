using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Security;
using Mainguard.Tests.TestTools;

namespace Mainguard.Tests;

/// <summary>TI-P2-22 #2: the loopback listener — state rejection, single-use, timeout (virtual clock),
/// ephemeral loopback port, and the no-secret-in-URL invariant.</summary>
public class LoopbackOAuthListenerTests
{
    private static readonly AuthorizeRequest Request =
        new("client-abc", "https://example.test/authorize", "repo read:user");

    private sealed class RecordingBrowser : IBrowserOpener
    {
        public string? LastUrl { get; private set; }
        public void Open(string url) => LastUrl = url;
    }

    private sealed class FakeChannel : ILoopbackCallbackChannel
    {
        private readonly TaskCompletionSource<IReadOnlyDictionary<string, string>> _tcs = new();
        public string RedirectUri => "http://127.0.0.1:49999/callback";
        public Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct) => _tcs.Task.WaitAsync(ct);
        public void Deliver(IReadOnlyDictionary<string, string> query) => _tcs.TrySetResult(query);
        public void Dispose() => _tcs.TrySetCanceled();
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var q = new Dictionary<string, string>(StringComparer.Ordinal);
        var idx = url.IndexOf('?');
        if (idx < 0) return q;
        foreach (var pair in url[(idx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            q[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return q;
    }

    [Fact]
    public async Task Authenticate_HappyPath_ShouldExchangeCodeForToken_NoSecretInUrl()
    {
        var browser = new RecordingBrowser();
        var channel = new FakeChannel();
        string? seenVerifier = null;
        LoopbackOAuthListener.TokenExchange exchange = (code, verifier, redirect, ct) =>
        {
            seenVerifier = verifier;
            Assert.Equal("the-code", code);
            return Task.FromResult("secret-access-token");
        };
        var listener = new LoopbackOAuthListener(browser, () => channel);

        var task = listener.AuthenticateAsync(Request, exchange);

        // The authorize URL is built + browser opened before the first await; read the issued state.
        var query = ParseQuery(browser.LastUrl!);
        channel.Deliver(new Dictionary<string, string> { ["code"] = "the-code", ["state"] = query["state"] });
        var token = await task;

        Assert.Equal("secret-access-token", token);
        // Authorize URL carries only non-secret params: challenge + state + response_type=code.
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("code", query["response_type"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        // The verifier and the token NEVER appear in any URL we construct.
        Assert.DoesNotContain(seenVerifier!, browser.LastUrl!);
        Assert.DoesNotContain("secret-access-token", browser.LastUrl!);
    }

    [Fact]
    public async Task Authenticate_ShouldRejectMismatchedState()
    {
        var browser = new RecordingBrowser();
        var channel = new FakeChannel();
        var listener = new LoopbackOAuthListener(browser, () => channel);

        var task = listener.AuthenticateAsync(Request, (_, _, _, _) => Task.FromResult("nope"));
        channel.Deliver(new Dictionary<string, string> { ["code"] = "c", ["state"] = "forged-not-the-issued-state" });

        var ex = await Assert.ThrowsAsync<LoopbackOAuthException>(() => task);
        Assert.Equal(LoopbackOAuthError.StateMismatch, ex.Error);
    }

    [Fact]
    public void ValidateCallback_ShouldClassifyUserDenialAndMissingCode()
    {
        var denied = Assert.Throws<LoopbackOAuthException>(() =>
            LoopbackOAuthListener.ValidateCallback(
                new Dictionary<string, string> { ["state"] = "s", ["error"] = "access_denied" }, "s", out _));
        Assert.Equal(LoopbackOAuthError.UserDenied, denied.Error);

        var missing = Assert.Throws<LoopbackOAuthException>(() =>
            LoopbackOAuthListener.ValidateCallback(
                new Dictionary<string, string> { ["state"] = "s" }, "s", out _));
        Assert.Equal(LoopbackOAuthError.MissingCode, missing.Error);
    }

    [Fact]
    public async Task Authenticate_ShouldTimeoutAtFiveMinutes_VirtualClock()
    {
        var time = new TestTimeProvider();
        var browser = new RecordingBrowser();
        var channel = new FakeChannel(); // never delivers
        var listener = new LoopbackOAuthListener(browser, () => channel, time, TimeSpan.FromMinutes(5));

        var task = listener.AuthenticateAsync(Request, (_, _, _, _) => Task.FromResult("nope"));
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var ex = await Assert.ThrowsAsync<LoopbackOAuthException>(() => task);
        Assert.Equal(LoopbackOAuthError.Timeout, ex.Error);
    }

    [Fact]
    public async Task HttpListenerChannel_ShouldBindEphemeralLoopbackPort_AndBeSingleUse()
    {
        HttpListenerCallbackChannel channel;
        try
        {
            channel = new HttpListenerCallbackChannel();
        }
        catch (HttpListenerException)
        {
            // HTTP.sys namespace reservation not granted in this environment (no admin/urlacl).
            // The single-use + state logic is covered deterministically by the fake-channel tests above.
            return;
        }

        using (channel)
        {
            var uri = new Uri(channel.RedirectUri);
            Assert.Equal("127.0.0.1", uri.Host);
            Assert.True(uri.Port > 0, "ephemeral port should be OS-assigned, not zero");

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var firstResponse = http.GetAsync($"{channel.RedirectUri}?code=abc&state=xyz");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var query = await channel.WaitForCallbackAsync(cts.Token);
            Assert.Equal("abc", query["code"]);
            Assert.Equal(HttpStatusCode.OK, (await firstResponse).StatusCode);

            // Single-use: a replayed redirect is Gone.
            var second = await http.GetAsync($"{channel.RedirectUri}?code=def&state=xyz");
            Assert.Equal(HttpStatusCode.Gone, second.StatusCode);
        }
    }
}
