using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.Git.Sync;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-22 Q1: GitLab signs in through the shared loopback OAuth + PKCE path (RFC 8252 + RFC 7636), not
/// the device flow. Fully offline — a fake browser, a fake single-use callback channel, and a fake
/// <see cref="HttpMessageHandler"/> for the token exchange (no network). Proves the loopback path is
/// used, the PKCE authorization-code grant is posted to the GitLab token endpoint, and the acquired
/// token stores under the landed keyring convention <c>token_&lt;host&gt;</c>.
/// </summary>
public class GitLabLoopbackProviderTests
{
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

    private sealed class StubTokenHandler : HttpMessageHandler
    {
        private readonly string _body;
        public Uri? LastRequestUri { get; private set; }
        public string? LastRequestContent { get; private set; }

        public StubTokenHandler(string body) => _body = body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            LastRequestContent = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            };
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "gitloom-gitlab-oauth-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
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
    public void GitLabProvider_UsesLoopbackAuthMethod_NotDeviceFlow()
    {
        var provider = new GitLabProvider("gitlab.com");
        Assert.Equal(HostAuthMethod.OAuthLoopback, provider.AuthMethod);
        Assert.False(provider.SupportsDeviceFlow);
        // A real registered client id is configured (the exact value is deployment config, not a test constant).
        Assert.False(string.IsNullOrWhiteSpace(GitLabProvider.DefaultClientId));
    }

    [Fact]
    public async Task AcquireToken_DrivesLoopbackPkceFlow_AndReturnsToken_NoNetwork()
    {
        var browser = new RecordingBrowser();
        var channel = new FakeChannel();
        var handler = new StubTokenHandler("{\"access_token\":\"glpat-loopback-xyz\"}");

        // The GitLab OAuth surface exactly as the provider builds it for gitlab.com.
        var config = new OAuthClientConfig(
            GitLabProvider.DefaultClientId,
            "https://gitlab.com/oauth/authorize",
            "https://gitlab.com/oauth/token",
            "read_repository write_repository api");
        var loopback = new OAuthLoopbackClient(config, browser, () => channel, handler);

        var provider = new GitLabProvider("gitlab.com", loopbackClient: loopback);

        // Start the flow; the browser is opened before the first await, so read the issued state, then
        // deliver the matching callback (mirrors the real single-use redirect).
        var task = provider.AcquireTokenAsync(CancellationToken.None);
        var authorizeQuery = ParseQuery(browser.LastUrl!);
        channel.Deliver(new Dictionary<string, string>
        {
            ["code"] = "auth-code-123",
            ["state"] = authorizeQuery["state"],
        });

        var token = await task;

        Assert.Equal("glpat-loopback-xyz", token);

        // Loopback path was used: the browser opened GitLab's authorize endpoint with an S256 PKCE
        // challenge and response_type=code (no secret in the URL).
        Assert.StartsWith("https://gitlab.com/oauth/authorize", browser.LastUrl);
        Assert.Equal("code", authorizeQuery["response_type"]);
        Assert.Equal("S256", authorizeQuery["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(authorizeQuery["code_challenge"]));

        // The token exchange POSTed the authorization_code grant + verifier to the GitLab token endpoint.
        Assert.Equal("https://gitlab.com/oauth/token", handler.LastRequestUri!.ToString());
        Assert.Contains("grant_type=authorization_code", handler.LastRequestContent);
        Assert.Contains("code_verifier=", handler.LastRequestContent);
        Assert.Contains("code=auth-code-123", handler.LastRequestContent);

        // And the acquired token stores under the landed token_<host> keyring convention.
        using var dir = new TempDir();
        var keyring = new SecureKeyring(dir.Path);
        keyring.SaveSecret(GitHostDetector.TokenKeyForHost("gitlab.com"), token);
        Assert.Equal("glpat-loopback-xyz", keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost("gitlab.com")));
    }

    [Fact]
    public async Task AcquireToken_WithoutBrowserOrChannel_ThrowsAuthRequired()
    {
        // Production path with no browser/loopback channel supplied (HostAuthContext.Empty) can't run
        // the browser flow — it fails with a typed AuthenticationRequiredException naming the host.
        var provider = new GitLabProvider("gitlab.com");
        var ex = await Assert.ThrowsAsync<Mainguard.Git.Exceptions.AuthenticationRequiredException>(
            () => provider.AcquireTokenAsync(CancellationToken.None));
        Assert.Equal("gitlab.com", ex.Host);
    }
}
