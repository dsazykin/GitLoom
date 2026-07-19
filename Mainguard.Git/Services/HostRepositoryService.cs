using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Hosting;
using Mainguard.Git.Models;
using Mainguard.Git.Security;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic "list my repositories" service (P2-48). Resolves the host's stored token from the
/// keyring (key <c>token_&lt;host&gt;</c> via <see cref="GitHostDetector.TokenKeyForHost"/>, with the
/// legacy <c>github_token</c> as a GitHub back-compat fallback — the same scheme the Accounts page uses)
/// and dispatches to the matching internal <see cref="IHostRepositoryProvider"/> (GitHub + GitLab
/// implemented; Bitbucket/Azure DevOps stubs). Structure mirrors <see cref="PullRequestService"/>.
///
/// <para>The <see cref="HttpClient"/> is <b>shared</b> across every call (never a per-call <c>new</c> —
/// socket exhaustion). SECURITY (G-4): the token is read from the keyring and handed to the provider,
/// which places it only in the <c>Authorization</c> header; it never enters a URL, argv, log, or
/// exception message here.</para>
/// </summary>
public sealed class HostRepositoryService : IHostRepositoryService
{
    private readonly ISecureKeyring _keyring;
    private readonly HttpClient _http;

    /// <param name="keyring">Token store; tests inject a temp-dir keyring.</param>
    /// <param name="httpClient">Optional shared client; tests inject one wrapping a fixture handler.</param>
    public HostRepositoryService(ISecureKeyring? keyring = null, HttpClient? httpClient = null)
    {
        _keyring = keyring ?? new SecureKeyring();
        _http = httpClient ?? new HttpClient();
    }

    public bool IsSupported(string host, HostKind kind)
    {
        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            return false;
        return !string.IsNullOrEmpty(TokenFor(host, kind));
    }

    public Task<IReadOnlyList<RemoteRepository>> ListMyRepositoriesAsync(string host, HostKind kind, CancellationToken ct)
    {
        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            throw new GitOperationException($"Listing repositories is not available for '{host}'.");

        var token = TokenFor(host, kind);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No stored token for {host}. Sign in to continue.", host);

        return provider.ListMyRepositoriesAsync(host, token, ct);
    }

    /// <summary>The stored token for a host (<c>token_&lt;host&gt;</c>), with the legacy GitHub fallback.</summary>
    private string? TokenFor(string host, HostKind kind)
    {
        var token = _keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost(host));
        if (string.IsNullOrEmpty(token) && kind == HostKind.GitHub)
            token = _keyring.RetrieveSecret("github_token");
        return token;
    }

    // Dispatch table (additive): a real provider per implemented host, a typed stub otherwise.
    private IHostRepositoryProvider? ResolveProvider(HostKind kind) => kind switch
    {
        HostKind.GitHub => new GitHubRepositoryProvider(_http),
        HostKind.GitLab => new GitLabRepositoryProvider(_http),
        HostKind.Bitbucket => new BitbucketRepositoryProvider(),
        HostKind.AzureDevOps => new AzureDevOpsRepositoryProvider(),
        _ => null,
    };
}
