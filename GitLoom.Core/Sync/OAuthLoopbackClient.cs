using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Security;

namespace GitLoom.Core.Sync;

/// <summary>A loopback OAuth client's host-specific endpoints + identity. PKCE public client, so there
/// is NO client secret here (RFC 8252 §8.5 / RFC 7636). <paramref name="ClientId"/> is the registered
/// OAuth application id; the authorize/token endpoints are the host's OAuth surface.</summary>
public sealed record OAuthClientConfig(string ClientId, string AuthorizeEndpoint, string TokenEndpoint, string Scope);

/// <summary>
/// The reusable OAuth 2.0 authorization-code + PKCE client for hosts that support a loopback redirect
/// (RFC 8252 + RFC 7636). It does NOT re-implement the receiver: it <b>composes</b> the shared
/// <see cref="LoopbackOAuthListener"/> (a second loopback listener anywhere is a rejection trigger) and
/// contributes only the host-specific <c>authorization_code</c> token-exchange POST. That POST sits
/// behind an <see cref="HttpMessageHandler"/> seam so the whole flow is exercised offline — mirroring
/// the <see cref="DeviceFlowClient"/> pattern.
///
/// <para>SECURITY (G-4): the PKCE verifier travels ONLY in the HTTPS token-exchange body (never any URL
/// we build, never argv, never logs); the returned access token is handed to the caller (who stores it
/// via <c>SecureKeyring</c>) and is never written here.</para>
/// </summary>
public sealed class OAuthLoopbackClient
{
    private readonly OAuthClientConfig _config;
    private readonly LoopbackOAuthListener _listener;
    private readonly HttpClient _httpClient;

    public OAuthLoopbackClient(
        OAuthClientConfig config,
        IBrowserOpener browser,
        Func<ILoopbackCallbackChannel> channelFactory,
        HttpMessageHandler? handler = null,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _listener = new LoopbackOAuthListener(browser, channelFactory, timeProvider, timeout);

        _httpClient = handler is null ? new HttpClient() : new HttpClient(handler);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitLoom", "1.0"));
    }

    /// <summary>Runs the browser + loopback + PKCE flow and returns the access token, or throws
    /// <see cref="LoopbackOAuthException"/> on any typed failure (timeout, state mismatch, user denial).</summary>
    public Task<string> AcquireTokenAsync(CancellationToken ct = default)
    {
        var request = new AuthorizeRequest(_config.ClientId, _config.AuthorizeEndpoint, _config.Scope);
        return _listener.AuthenticateAsync(request, ExchangeCodeAsync, ct);
    }

    /// <summary>The host-specific code→token exchange handed to the shared listener. PKCE public-client
    /// grant: client id (no secret), authorization code, the exact redirect URI, and the verifier.</summary>
    private async Task<string> ExchangeCodeAsync(string code, string codeVerifier, string redirectUri, CancellationToken ct)
    {
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", _config.ClientId),
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("redirect_uri", redirectUri),
            new KeyValuePair<string, string>("code_verifier", codeVerifier),
        });

        var response = await _httpClient.PostAsync(_config.TokenEndpoint, content, ct).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var tokenResponse = JsonSerializer.Deserialize<AccessTokenResponse>(json);

        if (string.IsNullOrEmpty(tokenResponse?.AccessToken))
        {
            // No secret in the message — surface only the (non-secret) provider error code, if any.
            throw new LoopbackOAuthException(LoopbackOAuthError.ProviderError,
                $"OAuth token exchange did not return an access token (error '{tokenResponse?.Error ?? "unknown"}').");
        }

        return tokenResponse.AccessToken;
    }
}
