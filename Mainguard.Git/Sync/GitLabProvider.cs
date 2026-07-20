using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;

namespace Mainguard.Git.Sync;

/// <summary>
/// GitLab host provider (T-14, P2-22 follow-up Q1): acquires a token via OAuth
/// authorization-code + PKCE over an ephemeral <c>127.0.0.1</c> loopback redirect
/// (RFC 8252 + RFC 7636), reusing the shared <see cref="OAuthLoopbackClient"/> (which
/// composes the one <see cref="Mainguard.Git.Security.LoopbackOAuthListener"/>). Works for
/// both gitlab.com and self-hosted GitLab — the authorize/token endpoints are derived from
/// <see cref="HostProviderBase.Host"/>, so a self-hosted instance authenticates against its
/// own origin. GitHub stays on the device flow (its OAuth apps don't support PKCE loopback).
/// </summary>
public sealed class GitLabProvider : HostProviderBase
{
    /// <summary>
    /// Placeholder OAuth application id. The owner registers the real GitLab OAuth application (a
    /// public PKCE client) with a <c>http://127.0.0.1:{ephemeral}/callback</c> redirect as part of the
    /// live-auth matrix, then supplies the real id through settings (<paramref name="clientId"/> ctor
    /// arg). The loopback wiring/behavior are complete offline.
    /// </summary>
    // GitLoom's registered GitLab OAuth application (public PKCE client — safe to ship; no secret).
    // A self-hosted GitLab instance can override this via the clientId ctor arg / settings.
    public const string DefaultClientId = "e464a27cf2ee60770c37c79fb41769c7aae947bc360754046ee6876e68a3f3ed";

    private readonly HostAuthContext _context;
    private readonly OAuthLoopbackClient? _loopback;
    private readonly string _clientId;

    public GitLabProvider(
        string host = "gitlab.com",
        HostAuthContext? context = null,
        OAuthLoopbackClient? loopbackClient = null,
        string? clientId = null)
        : base(string.IsNullOrEmpty(host) ? "gitlab.com" : host, HostKind.GitLab)
    {
        _context = context ?? HostAuthContext.Empty;
        _loopback = loopbackClient;
        _clientId = string.IsNullOrEmpty(clientId) ? DefaultClientId : clientId;
    }

    public override HostAuthMethod AuthMethod => HostAuthMethod.OAuthLoopback;

    public override async Task<string> AcquireTokenAsync(CancellationToken ct)
    {
        // TODO(P2-22 Q1 human-review): live auth matrix — real GitLab loopback+PKCE browser round trip
        // (requires the registered GitLab OAuth application id + its 127.0.0.1 redirect).
        var client = _loopback ?? BuildLoopbackClient();

        var token = await client.AcquireTokenAsync(ct);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"GitLab loopback authentication for {Host} did not complete.", Host);

        return token;
    }

    // Production path: build the loopback client from the App-supplied browser opener + channel factory.
    private OAuthLoopbackClient BuildLoopbackClient()
    {
        if (_context.BrowserOpener is null || _context.LoopbackChannelFactory is null)
            throw new AuthenticationRequiredException(
                $"A browser-based sign-in is required for {Host}, but no browser/loopback channel was supplied.", Host);

        var baseUrl = $"https://{Host}";
        var config = new OAuthClientConfig(
            _clientId,
            $"{baseUrl}/oauth/authorize",
            $"{baseUrl}/oauth/token",
            "read_repository write_repository api");

        return new OAuthLoopbackClient(config, _context.BrowserOpener, _context.LoopbackChannelFactory);
    }
}
