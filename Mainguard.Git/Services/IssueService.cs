using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Issues;
using Mainguard.Git.Models;
using Mainguard.Git.Security;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic issue-tracking service (T-24). Sibling of <see cref="PullRequestService"/>: it resolves
/// the repo's origin host + stored token and parses <c>owner/repo</c> from the remote through the <b>same
/// shared</b> <see cref="HostConnectionResolver"/> (no duplicate host/token resolver), then dispatches to
/// the matching internal <see cref="IIssueProvider"/> (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs).
///
/// <para>The <see cref="HttpClient"/> is <b>shared</b> across every call (never a per-call <c>new</c> —
/// socket exhaustion). SECURITY (G-4): the token is read from the keyring and handed to the provider,
/// which places it only in the <c>Authorization</c> header; it never enters a URL, argv, log, or
/// exception message here.</para>
/// </summary>
public sealed class IssueService : IIssueService
{
    private readonly HostConnectionResolver _resolver;
    private readonly HttpClient _http;

    /// <param name="httpClient">Optional shared client; tests inject one wrapping a fixture handler.</param>
    public IssueService(IGitService git, ISecureKeyring? keyring = null, HttpClient? httpClient = null)
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

    public Task<IReadOnlyList<IssueItem>> ListAsync(string repoPath, IssueState filter, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.ListAsync(slug, token, filter, ct);
    }

    public Task<IssueDetail> GetAsync(string repoPath, int number, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.GetAsync(slug, token, number, ct);
    }

    public Task<IssueItem> CreateAsync(string repoPath, CreateIssue request, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.CreateAsync(slug, token, request, ct);
    }

    public Task<IssueComment> CommentAsync(string repoPath, int number, string body, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.CommentAsync(slug, token, number, body, ct);
    }

    public Task<IssueItem> SetStateAsync(string repoPath, int number, IssueState state, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.SetStateAsync(slug, token, number, state, ct);
    }

    // Resolves (provider, owner/repo, token) or throws a typed error. Central so no operation reaches a
    // provider without a validated host, parsed slug, and a token. Host/token/slug plumbing is the shared
    // HostConnectionResolver (same path as PullRequestService); only the provider dispatch is issue-specific.
    private (IIssueProvider Provider, RepoSlug Slug, string Token) Resolve(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out var remoteUrl))
            throw new GitOperationException("This repository has no origin remote pointing at a supported host.");

        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            throw new GitOperationException($"Issue tracking is not available for '{host}'.");

        var slug = HostConnectionResolver.ParseSlug(remoteUrl)
            ?? throw new GitOperationException($"Could not parse an owner/repository from the origin URL for '{host}'.");

        var token = _resolver.TokenFor(host);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No stored token for {host}. Sign in to continue.", host);

        return (provider, slug, token);
    }

    // Dispatch table (additive): a real provider per implemented host, a typed stub otherwise.
    private IIssueProvider? ResolveProvider(HostKind kind) => kind switch
    {
        HostKind.GitHub => new GitHubIssueProvider(_http),
        HostKind.GitLab => new GitLabIssueProvider(),
        HostKind.Bitbucket => new BitbucketIssueProvider(),
        HostKind.AzureDevOps => new AzureDevOpsIssueProvider(),
        _ => null,
    };
}
