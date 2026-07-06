using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Services; // shared RepoSlug

namespace GitLoom.Core.Commits;

/// <summary>
/// Base for not-yet-implemented commit-context providers (T-32). The dispatch table stays complete so
/// wiring a real provider is additive, but the operation throws a typed "not yet supported for &lt;host&gt;"
/// and <see cref="IsImplemented"/> is false so <c>CommitContextService.IsSupported</c> reports the host as
/// unsupported (the blame gutter then hides the jump action rather than erroring).
/// </summary>
internal abstract class UnsupportedCommitContextProvider : ICommitContextProvider
{
    protected abstract string HostLabel { get; }

    public bool IsImplemented => false;

    public Task<CommitContextResult> GetForCommitAsync(RepoSlug repo, string token, string sha, CancellationToken ct)
        => throw new GitOperationException($"Blame → pull request is not yet supported for {HostLabel}.");
}

/// <summary>GitLab commit-context provider stub (T-32): <c>/projects/:id/repository/commits/:sha/merge_requests</c> lands with the live matrix.</summary>
internal sealed class GitLabCommitContextProvider : UnsupportedCommitContextProvider
{
    protected override string HostLabel => "GitLab";
}

/// <summary>Bitbucket commit-context provider stub (T-32).</summary>
internal sealed class BitbucketCommitContextProvider : UnsupportedCommitContextProvider
{
    protected override string HostLabel => "Bitbucket";
}

/// <summary>Azure DevOps commit-context provider stub (T-32).</summary>
internal sealed class AzureDevOpsCommitContextProvider : UnsupportedCommitContextProvider
{
    protected override string HostLabel => "Azure DevOps";
}
