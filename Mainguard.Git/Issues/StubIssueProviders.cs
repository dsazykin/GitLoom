using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug

namespace Mainguard.Git.Issues;

/// <summary>
/// Base for not-yet-implemented issue providers (T-24). The dispatch table stays complete so wiring a
/// real provider is additive, but every operation throws a typed "not yet supported for &lt;host&gt;" and
/// <see cref="IsImplemented"/> is false so <c>IssueService.IsSupported</c> reports the host as unsupported
/// (the UI then shows the graceful unsupported affordance rather than erroring).
/// </summary>
internal abstract class UnsupportedIssueProvider : IIssueProvider
{
    protected abstract string HostLabel { get; }

    public bool IsImplemented => false;

    private Exception NotSupported() =>
        new GitOperationException($"Issue tracking is not yet supported for {HostLabel}.");

    public Task<IReadOnlyList<IssueItem>> ListAsync(RepoSlug repo, string token, IssueState filter, CancellationToken ct) => throw NotSupported();
    public Task<IssueDetail> GetAsync(RepoSlug repo, string token, int number, CancellationToken ct) => throw NotSupported();
    public Task<IssueItem> CreateAsync(RepoSlug repo, string token, CreateIssue request, CancellationToken ct) => throw NotSupported();
    public Task<IssueComment> CommentAsync(RepoSlug repo, string token, int number, string body, CancellationToken ct) => throw NotSupported();
    public Task<IssueItem> SetStateAsync(RepoSlug repo, string token, int number, IssueState state, CancellationToken ct) => throw NotSupported();
}

/// <summary>GitLab issues provider stub (T-24): <c>/projects/:id/issues</c> lands with the live matrix.</summary>
internal sealed class GitLabIssueProvider : UnsupportedIssueProvider
{
    protected override string HostLabel => "GitLab";
}

/// <summary>Bitbucket issues provider stub (T-24).</summary>
internal sealed class BitbucketIssueProvider : UnsupportedIssueProvider
{
    protected override string HostLabel => "Bitbucket";
}

/// <summary>Azure DevOps work-items provider stub (T-24).</summary>
internal sealed class AzureDevOpsIssueProvider : UnsupportedIssueProvider
{
    protected override string HostLabel => "Azure DevOps";
}
