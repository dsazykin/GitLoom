using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Models;
using GitLoom.Core.Security;

namespace GitLoom.Core.Sync;

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
public interface IHostProvider
{
    /// <summary>The host this provider authenticates against (e.g. <c>github.com</c>, a self-hosted GitLab).</summary>
    string Host { get; }

    /// <summary>Which host family this provider serves (drives the username convention).</summary>
    HostKind Kind { get; }

    /// <summary><c>true</c> when the provider acquires tokens via OAuth device flow; <c>false</c> for PAT-dialog providers.</summary>
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
    public abstract bool SupportsDeviceFlow { get; }

    // SINGLE SOURCE: never a local host→username switch — always GitHostDetector.
    public string TokenUsername => GitHostDetector.UsernameForToken(Kind);

    public abstract Task<string> AcquireTokenAsync(CancellationToken ct);
}
