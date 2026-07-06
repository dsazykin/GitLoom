using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Services; // shared RepoSlug

namespace GitLoom.Core.Checks;

/// <summary>
/// Base for not-yet-implemented CI/checks providers (T-26). The dispatch table stays complete so wiring a
/// real provider is additive, but every operation throws a typed "not yet supported for &lt;host&gt;" and
/// <see cref="IsImplemented"/> is false so <c>CheckStatusService.IsSupported</c> reports the host as
/// unsupported (the UI then shows the graceful unsupported affordance rather than erroring).
/// </summary>
internal abstract class UnsupportedCheckProvider : ICheckProvider
{
    protected abstract string HostLabel { get; }

    public bool IsImplemented => false;

    private Exception NotSupported() =>
        new GitOperationException($"CI checks are not yet supported for {HostLabel}.");

    public Task<CommitChecks> GetChecksAsync(RepoSlug repo, string token, string sha, CancellationToken ct) => throw NotSupported();
    public Task RerequestAsync(RepoSlug repo, string token, long checkRunId, CancellationToken ct) => throw NotSupported();
}

/// <summary>GitLab pipelines provider stub (T-26): <c>/projects/:id/pipelines</c> lands with the live matrix.</summary>
internal sealed class GitLabCheckProvider : UnsupportedCheckProvider
{
    protected override string HostLabel => "GitLab";
}

/// <summary>Bitbucket build-status provider stub (T-26).</summary>
internal sealed class BitbucketCheckProvider : UnsupportedCheckProvider
{
    protected override string HostLabel => "Bitbucket";
}

/// <summary>Azure DevOps pipelines provider stub (T-26).</summary>
internal sealed class AzureDevOpsCheckProvider : UnsupportedCheckProvider
{
    protected override string HostLabel => "Azure DevOps";
}
