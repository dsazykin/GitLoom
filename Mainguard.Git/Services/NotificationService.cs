using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Notifications;
using Mainguard.Git.Security;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic notifications-inbox service (T-27). Sibling of <see cref="CheckStatusService"/> /
/// <see cref="IssueService"/> / <see cref="PullRequestService"/>: it resolves the repo's origin host +
/// stored token through the <b>same shared</b> <see cref="HostConnectionResolver"/> (no duplicate
/// host/token resolver), then dispatches to the matching internal <see cref="INotificationProvider"/>
/// (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs). No <c>owner/repo</c> slug is needed — notifications
/// are the authenticated user's, scoped only by the token.
///
/// <para>The <see cref="HttpClient"/> is <b>shared</b> across every call (never a per-call <c>new</c> —
/// socket exhaustion). SECURITY (G-4): the token is read from the keyring and handed to the provider,
/// which places it only in the <c>Authorization</c> header; it never enters a URL, argv, log, or exception
/// message here.</para>
/// </summary>
public sealed class NotificationService : INotificationService
{
    private readonly HostConnectionResolver _resolver;
    private readonly HttpClient _http;

    /// <param name="httpClient">Optional shared client; tests inject one wrapping a fixture handler.</param>
    public NotificationService(IGitService git, ISecureKeyring? keyring = null, HttpClient? httpClient = null)
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

    public Task<IReadOnlyList<NotificationItem>> ListAsync(string repoPath, bool onlyUnread, CancellationToken ct)
    {
        var (provider, token) = Resolve(repoPath);
        return provider.ListAsync(token, onlyUnread, ct);
    }

    public Task MarkReadAsync(string repoPath, string threadId, CancellationToken ct)
    {
        var (provider, token) = Resolve(repoPath);
        return provider.MarkReadAsync(token, threadId, ct);
    }

    public Task MarkAllReadAsync(string repoPath, CancellationToken ct)
    {
        var (provider, token) = Resolve(repoPath);
        return provider.MarkAllReadAsync(token, ct);
    }

    // Resolves (provider, token) or throws a typed error. Central so no operation reaches a provider without
    // a validated host and a token. Host/token plumbing is the shared HostConnectionResolver (same path as
    // the PR/issue/checks services); only the provider dispatch is notifications-specific. No slug needed —
    // notifications are user-scoped.
    private (INotificationProvider Provider, string Token) Resolve(string repoPath)
    {
        if (!_resolver.TryResolveHost(repoPath, out var host, out var kind, out _))
            throw new GitOperationException("This repository has no origin remote pointing at a supported host.");

        var provider = ResolveProvider(kind);
        if (provider is not { IsImplemented: true })
            throw new GitOperationException($"Notifications are not available for '{host}'.");

        var token = _resolver.TokenFor(host);
        if (string.IsNullOrEmpty(token))
            throw new AuthenticationRequiredException($"No stored token for {host}. Sign in to continue.", host);

        return (provider, token);
    }

    // Dispatch table (additive): a real provider per implemented host, a typed stub otherwise.
    private INotificationProvider? ResolveProvider(HostKind kind) => kind switch
    {
        HostKind.GitHub => new GitHubNotificationProvider(_http),
        HostKind.GitLab => new GitLabNotificationProvider(),
        HostKind.Bitbucket => new BitbucketNotificationProvider(),
        HostKind.AzureDevOps => new AzureDevOpsNotificationProvider(),
        _ => null,
    };
}
