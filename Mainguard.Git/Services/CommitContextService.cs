using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Commits;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Security;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic commit-context service (T-32). Sibling of <see cref="PullRequestService"/> /
/// <see cref="IssueService"/>: it resolves the repo's origin host + stored token and parses
/// <c>owner/repo</c> from the remote through the <b>same shared</b> <see cref="HostConnectionResolver"/>
/// (no duplicate host/token resolver — that's a rejection trigger), then dispatches to the matching
/// internal <see cref="ICommitContextProvider"/> (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs).
///
/// <para>The <see cref="HttpClient"/> is <b>shared</b> across every call (never a per-call <c>new</c> —
/// socket exhaustion). SECURITY (G-4): the token is read from the keyring and handed to the provider,
/// which places it only in the <c>Authorization</c> header; it never enters a URL, argv, log, or
/// exception message here.</para>
/// </summary>
public sealed class CommitContextService : ICommitContextService
{
    private readonly HostConnectionResolver _resolver;
    private readonly HttpClient _http;

    /// <param name="httpClient">Optional shared client; tests inject one wrapping a fixture handler.</param>
    public CommitContextService(IGitService git, ISecureKeyring? keyring = null, HttpClient? httpClient = null)
    {
        if (git is null) throw new ArgumentNullException(nameof(git));
        _resolver = new HostConnectionResolver(git, keyring ?? new SecureKeyring());
        _http = httpClient ?? new HttpClient();
    }

    public bool IsSupported(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out _))
            return false;
        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            return false;
        return !string.IsNullOrEmpty(_resolver.TokenFor(host));
    }

    public Task<CommitContextResult> GetForCommitAsync(string repoPath, string sha, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.GetForCommitAsync(slug, token, sha, ct);
    }

    // Resolves (provider, owner/repo, token) or throws a typed error. Central so no operation reaches a
    // provider without a validated host, parsed slug, and a token. Host/token/slug plumbing is the shared
    // HostConnectionResolver (same path as PullRequestService/IssueService); only the dispatch is context-specific.
    private (ICommitContextProvider Provider, RepoSlug Slug, string Token) Resolve(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out var remoteUrl))
            throw new GitOperationException("This repository has no origin remote pointing at a supported host.");

        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            throw new GitOperationException($"Blame → pull request is not available for '{host}'.");

        var slug = HostConnectionResolver.ParseSlug(remoteUrl)
            ?? throw new GitOperationException($"Could not parse an owner/repository from the origin URL for '{host}'.");

        var token = _resolver.TokenFor(host);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No stored token for {host}. Sign in to continue.", host);

        return (provider, slug, token);
    }

    // Dispatch table (additive): a real provider per implemented host, a typed stub otherwise.
    private ICommitContextProvider? ResolveProvider(HostKind kind) => kind switch
    {
        HostKind.GitHub => new GitHubCommitContextProvider(_http),
        HostKind.GitLab => new GitLabCommitContextProvider(),
        HostKind.Bitbucket => new BitbucketCommitContextProvider(),
        HostKind.AzureDevOps => new AzureDevOpsCommitContextProvider(),
        _ => null,
    };
}
