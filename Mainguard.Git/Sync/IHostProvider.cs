using System;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Git.Security;

namespace Mainguard.Git.Sync;

/// <summary>
/// A Git hosting provider's authentication strategy (T-14). One concrete provider
/// per host kind knows how to acquire a token — GitHub/GitLab via OAuth device
/// flow, everyone else via a personal-access-token dialog — and the username
/// convention that host expects for token auth.
///
/// <para>SECURITY (G-4): a token acquired here is only ever handed to
/// <c>SecureKeyring</c> and fed to git through its credential mechanism / child
/// process env. It is never placed in a URL, a network-command argv, a log, or an
/// exception message.</para>
/// </summary>
/// <summary>
/// How a host provider acquires an access token. Exactly one per provider; the Accounts UI branches
/// on it (device-flow shows a code, loopback opens the browser, PAT reveals a paste field).
/// </summary>
public enum HostAuthMethod
{
    /// <summary>OAuth 2.0 device-authorization grant (RFC 8628). GitHub — its OAuth apps don't support
    /// PKCE loopback — stays on this path.</summary>
    OAuthDeviceFlow,

    /// <summary>OAuth 2.0 authorization-code + PKCE via an ephemeral <c>127.0.0.1</c> redirect
    /// (RFC 8252 + RFC 7636). GitLab / generic OIDC hosts use this.</summary>
    OAuthLoopback,

    /// <summary>A personal access token the user pastes into a dialog (no OAuth app registered).</summary>
    PersonalAccessToken,
}

public interface IHostProvider
{
    /// <summary>The host this provider authenticates against (e.g. <c>github.com</c>, a self-hosted GitLab).</summary>
    string Host { get; }

    /// <summary>Which host family this provider serves (drives the username convention).</summary>
    HostKind Kind { get; }

    /// <summary>How this provider acquires a token (device flow / loopback OAuth / pasted PAT).</summary>
    HostAuthMethod AuthMethod { get; }

    /// <summary><c>true</c> when the provider acquires tokens via OAuth device flow; <c>false</c> otherwise.
    /// Derived from <see cref="AuthMethod"/> — kept for the existing device-flow call sites.</summary>
    bool SupportsDeviceFlow { get; }

    /// <summary>
    /// Username git must use with this host's token. <b>Single source of truth</b>:
    /// always delegates to <see cref="GitHostDetector.UsernameForToken"/> — there is
    /// no per-provider username switch anywhere (a duplicate would diverge from the
    /// value <c>RunGitCheckedAuthenticated</c> uses on the real auth path).
    /// </summary>
    string TokenUsername { get; }

    /// <summary>
    /// Acquires an access token: runs the device flow (device providers) or resolves
    /// the token the user pasted into the PAT dialog (PAT providers). Throws
    /// <c>AuthenticationRequiredException</c> if no token could be obtained.
    /// </summary>
    Task<string> AcquireTokenAsync(CancellationToken ct);
}

/// <summary>
/// UI callbacks a provider may need while acquiring a token: presenting a device
/// code to the user, or prompting them to paste a PAT. Injected by the registry so
/// Core stays UI-free and providers stay unit-testable (tests supply fakes).
/// </summary>
public sealed class HostAuthContext
{
    public static readonly HostAuthContext Empty = new();

    /// <summary>Invoked with the device-flow user code + verification URL so the UI can display them.</summary>
    public Func<DeviceFlowResponse, Task>? PresentDeviceCode { get; init; }

    /// <summary>Invoked to obtain a personal access token for the given host (returns null if cancelled).</summary>
    public Func<string, CancellationToken, Task<string?>>? PromptForPat { get; init; }

    /// <summary>Opens the authorize URL through the OS browser for a loopback OAuth flow (RFC 8252).
    /// Supplied by the App (its <c>BrowserOpener</c> adapter); Core stays UI-free.</summary>
    public IBrowserOpener? BrowserOpener { get; init; }

    /// <summary>Builds a fresh single-use loopback callback receiver for a loopback OAuth flow. Supplied
    /// by the App (<c>() =&gt; new HttpListenerCallbackChannel()</c>); a fake in tests.</summary>
    public Func<ILoopbackCallbackChannel>? LoopbackChannelFactory { get; init; }
}

/// <summary>
/// Shared base: implements the single-source-of-truth <see cref="TokenUsername"/>
/// so no concrete provider ever re-derives the host→username mapping.
/// </summary>
public abstract class HostProviderBase : IHostProvider
{
    protected HostProviderBase(string host, HostKind kind)
    {
        Host = host;
        Kind = kind;
    }

    public string Host { get; }
    public HostKind Kind { get; }
    public abstract HostAuthMethod AuthMethod { get; }

    // Derived once from AuthMethod — no concrete provider re-states it.
    public bool SupportsDeviceFlow => AuthMethod == HostAuthMethod.OAuthDeviceFlow;

    // SINGLE SOURCE: never a local host→username switch — always GitHostDetector.
    public string TokenUsername => GitHostDetector.UsernameForToken(Kind);

    public abstract Task<string> AcquireTokenAsync(CancellationToken ct);
}
