using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.PullRequests;
using GitLoom.Core.Security;

namespace GitLoom.Core.Services;

/// <summary>
/// Host-agnostic pull-request service (T-23). Resolves the repo's origin host + stored token,
/// parses <c>owner/repo</c> from the remote once, and dispatches to the matching internal
/// <see cref="IPullRequestProvider"/> (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs).
///
/// <para>The <see cref="HttpClient"/> is <b>shared</b> across every call (never a per-call
/// <c>new</c> — socket exhaustion). SECURITY (G-4): the token is read from the keyring and handed
/// to the provider, which places it only in the <c>Authorization</c> header; it never enters a URL,
/// argv, log, or exception message here.</para>
/// </summary>
public sealed class PullRequestService : IPullRequestService
{
    private readonly IGitService _git;
    private readonly ISecureKeyring _keyring;
    private readonly HttpClient _http;

    /// <param name="httpClient">Optional shared client; tests inject one wrapping a fixture handler.</param>
    public PullRequestService(IGitService git, ISecureKeyring? keyring = null, HttpClient? httpClient = null)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _keyring = keyring ?? new SecureKeyring();
        _http = httpClient ?? new HttpClient();
    }

    public bool IsSupported(string repoPath)
    {
        if (!TryResolveHost(repoPath, out var host, out var kind, out _))
            return false;
        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            return false;
        return !string.IsNullOrEmpty(TokenFor(host));
    }

    public Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.ListAsync(slug, token, filter, ct);
    }

    public Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.GetAsync(slug, token, number, ct);
    }

    public Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.CreateAsync(slug, token, request, ct);
    }

    public Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.MergeAsync(slug, token, number, method, ct);
    }

    public Task CloseAsync(string repoPath, int number, CancellationToken ct)
    {
        var (provider, slug, token) = Resolve(repoPath);
        return provider.CloseAsync(slug, token, number, ct);
    }

    // Resolves (provider, owner/repo, token) or throws a typed error. Central so no operation
    // reaches a provider without a validated host, parsed slug, and a token.
    private (IPullRequestProvider Provider, RepoSlug Slug, string Token) Resolve(string repoPath)
    {
        if (!TryResolveHost(repoPath, out var host, out var kind, out var remoteUrl))
            throw new GitOperationException("This repository has no origin remote pointing at a supported host.");

        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            throw new GitOperationException($"Pull request integration is not available for '{host}'.");

        var slug = GitHostDetector.ParseOwnerRepo(remoteUrl)
            ?? throw new GitOperationException($"Could not parse an owner/repository from the origin URL for '{host}'.");

        var token = TokenFor(host);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No stored token for {host}. Sign in to continue.", host);

        return (provider, new RepoSlug(slug.Owner, slug.Repo), token);
    }

    // Reads the origin (else default, else sole) remote and classifies its host.
    private bool TryResolveHost(string repoPath, out string host, out HostKind kind, out string remoteUrl)
    {
        host = ""; kind = HostKind.Unknown; remoteUrl = "";
        try
        {
            var remotes = _git.GetRemotes(repoPath);
            var remote = remotes.FirstOrDefault(r => string.Equals(r.Name, "origin", StringComparison.Ordinal))
                         ?? remotes.FirstOrDefault();
            if (remote is null || string.IsNullOrWhiteSpace(remote.FetchUrl)) return false;

            remoteUrl = remote.FetchUrl;
            (host, kind) = GitHostDetector.Detect(remoteUrl);
            return !string.IsNullOrEmpty(host);
        }
        catch
        {
            // A repo whose remotes can't be read is simply "unsupported" — never throws from IsSupported.
            return false;
        }
    }

    private string? TokenFor(string host) => _keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost(host));

    // Dispatch table (additive): a real provider per implemented host, a typed stub otherwise.
    private IPullRequestProvider? ResolveProvider(HostKind kind) => kind switch
    {
        HostKind.GitHub => new GitHubPullRequestProvider(_http),
        HostKind.GitLab => new GitLabPullRequestProvider(),
        HostKind.Bitbucket => new BitbucketPullRequestProvider(),
        HostKind.AzureDevOps => new AzureDevOpsPullRequestProvider(),
        _ => null,
    };
}
