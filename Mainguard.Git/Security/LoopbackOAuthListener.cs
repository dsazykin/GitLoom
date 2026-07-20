using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Mainguard.Git.Security;

/// <summary>Why a loopback OAuth attempt failed. Every abort is one of these — never an untyped throw.</summary>
public enum LoopbackOAuthError
{
    /// <summary>No callback arrived inside the window (default 5 min); the port was released.</summary>
    Timeout,
    /// <summary>The callback's <c>state</c> did not match the one we issued (CSRF / stray hit) — flow aborted.</summary>
    StateMismatch,
    /// <summary>The provider redirected with <c>error=access_denied</c> (the user declined consent).</summary>
    UserDenied,
    /// <summary>The callback carried neither <c>code</c> nor a recognized <c>error</c>.</summary>
    MissingCode,
    /// <summary>The provider redirected with some other <c>error=</c> value.</summary>
    ProviderError,
}

/// <summary>The typed failure of a loopback OAuth flow. Carries no secret material in its message.</summary>
public sealed class LoopbackOAuthException : Exception
{
    public LoopbackOAuthError Error { get; }

    public LoopbackOAuthException(LoopbackOAuthError error, string message)
        : base(message) => Error = error;
}

/// <summary>The single place a URL is handed to the OS browser, injected so Core stays UI-free and the
/// flow is testable. The App supplies an adapter over its one <c>BrowserLauncher</c> (no second launcher).</summary>
public interface IBrowserOpener
{
    void Open(string url);
}

/// <summary>
/// The loopback receiver seam (RFC 8252 §7.3): binds an ephemeral <c>127.0.0.1</c> port and awaits
/// exactly one browser redirect. Kept behind an interface so the state/timeout/single-use logic is
/// unit-tested with a fake while <see cref="HttpListenerCallbackChannel"/> carries the real socket.
/// </summary>
public interface ILoopbackCallbackChannel : IDisposable
{
    /// <summary>The <c>http://127.0.0.1:{ephemeral}/callback</c> URI to register as the redirect.</summary>
    string RedirectUri { get; }

    /// <summary>Awaits the FIRST callback and returns its parsed query. Any later hit is served 410 and
    /// never observed here (single-use).</summary>
    Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken ct);
}

/// <summary>An authorize request: the host's endpoint + client id + scope. No secret ever appears here.</summary>
public sealed record AuthorizeRequest(string ClientId, string AuthorizeEndpoint, string Scope);

/// <summary>
/// The ONE loopback + PKCE token flow (RFC 8252 + RFC 7636) every git-host provider that supports a
/// loopback redirect uses. A second loopback listener implementation anywhere is a rejection trigger.
///
/// <para>Flow: generate PKCE + <c>state</c> → open the authorize URL through the injected browser opener
/// → await exactly one callback ≤ timeout (default 5 min, single-use) → validate <c>state</c> in
/// constant time → hand <c>(code, verifier, redirectUri)</c> to the caller's host-specific exchange for
/// the token. The verifier never enters any URL we build and is never logged; the token is returned to
/// the caller (who stores it via <see cref="ISecureKeyStore"/>) and is never logged here.</para>
/// </summary>
public sealed class LoopbackOAuthListener
{
    /// <summary>The host-specific code→token exchange. Receives the authorization code, the PKCE
    /// verifier, and the exact redirect URI used; returns the access token (HTTPS body only).</summary>
    public delegate Task<string> TokenExchange(string code, string codeVerifier, string redirectUri, CancellationToken ct);

    /// <summary>RFC 8252 recommends a bounded wait; the master doc pins it at 5 minutes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly IBrowserOpener _browser;
    private readonly Func<ILoopbackCallbackChannel> _channelFactory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _timeout;

    public LoopbackOAuthListener(
        IBrowserOpener browser,
        Func<ILoopbackCallbackChannel> channelFactory,
        TimeProvider? timeProvider = null,
        TimeSpan? timeout = null)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _channelFactory = channelFactory ?? throw new ArgumentNullException(nameof(channelFactory));
        _time = timeProvider ?? TimeProvider.System;
        _timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>Runs the whole flow and returns the access token, or throws <see cref="LoopbackOAuthException"/>.</summary>
    public async Task<string> AuthenticateAsync(AuthorizeRequest request, TokenExchange exchange, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(exchange);

        var pkce = Pkce.CreatePair();
        var state = NewState();

        using var channel = _channelFactory();
        var authorizeUrl = BuildAuthorizeUrl(request, channel.RedirectUri, state, pkce.Challenge);
        _browser.Open(authorizeUrl);

        // A single-use, bounded wait. CancellationTokenSource(delay, timeProvider) lets a virtual clock
        // drive the 5-minute timeout deterministically in tests; the port is released when the channel
        // is disposed on exit either way.
        using var timeoutCts = new CancellationTokenSource(_timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        IReadOnlyDictionary<string, string> callback;
        try
        {
            callback = await channel.WaitForCallbackAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new LoopbackOAuthException(LoopbackOAuthError.Timeout,
                $"No OAuth callback within {_timeout.TotalMinutes:0} minutes; the loopback port was released.");
        }

        ValidateCallback(callback, state, out var code);
        return await exchange(code, pkce.Verifier, channel.RedirectUri, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Validates a callback query against the issued <c>state</c> and extracts the code. Pure and
    /// public so the state-rejection / user-denial matrix is tested without any socket. Constant-time
    /// state comparison avoids leaking match progress.
    /// </summary>
    public static void ValidateCallback(IReadOnlyDictionary<string, string> callback, string expectedState, out string code)
    {
        ArgumentNullException.ThrowIfNull(callback);

        callback.TryGetValue("state", out var gotState);
        if (!FixedTimeEquals(gotState, expectedState))
        {
            throw new LoopbackOAuthException(LoopbackOAuthError.StateMismatch,
                "OAuth callback state did not match; the flow was aborted.");
        }

        if (callback.TryGetValue("error", out var error) && !string.IsNullOrEmpty(error))
        {
            var kind = string.Equals(error, "access_denied", StringComparison.OrdinalIgnoreCase)
                ? LoopbackOAuthError.UserDenied
                : LoopbackOAuthError.ProviderError;
            throw new LoopbackOAuthException(kind, $"OAuth provider returned error '{error}'.");
        }

        if (!callback.TryGetValue("code", out var got) || string.IsNullOrEmpty(got))
        {
            throw new LoopbackOAuthException(LoopbackOAuthError.MissingCode,
                "OAuth callback carried no authorization code.");
        }

        code = got;
    }

    /// <summary>Builds the authorize URL. Contains only non-secret params (client id, redirect, scope,
    /// state, S256 challenge) — never the verifier or any token.</summary>
    public static string BuildAuthorizeUrl(AuthorizeRequest request, string redirectUri, string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = request.ClientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = "code",
            ["scope"] = request.Scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };

        var sb = new StringBuilder(request.AuthorizeEndpoint);
        sb.Append(request.AuthorizeEndpoint.Contains('?') ? '&' : '?');
        var first = true;
        foreach (var (k, v) in query)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }

    private static string NewState() => Pkce.Base64Url(RandomNumberGenerator.GetBytes(16));

    private static bool FixedTimeEquals(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var ab = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }
}
