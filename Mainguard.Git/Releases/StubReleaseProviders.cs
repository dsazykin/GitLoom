using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug

namespace Mainguard.Git.Releases;

/// <summary>
/// Base for not-yet-implemented releases providers (T-28). The dispatch table stays complete so wiring a
/// real provider is additive, but every operation throws a typed "not yet supported for &lt;host&gt;" and
/// <see cref="IsImplemented"/> is false so <c>ReleaseService.IsSupported</c> reports the host as unsupported
/// (the UI then shows the graceful unsupported affordance rather than erroring).
/// </summary>
internal abstract class UnsupportedReleaseProvider : IReleaseProvider
{
    protected abstract string HostLabel { get; }

    public bool IsImplemented => false;

    private Exception NotSupported() =>
        new GitOperationException($"Releases are not yet supported for {HostLabel}.");

    public Task<IReadOnlyList<ReleaseItem>> ListAsync(RepoSlug repo, string token, CancellationToken ct) => throw NotSupported();
    public Task<ReleaseItem> CreateAsync(RepoSlug repo, string token, CreateRelease request, CancellationToken ct) => throw NotSupported();
}

/// <summary>GitLab releases provider stub (T-28): <c>/projects/:id/releases</c> lands with the live matrix.</summary>
internal sealed class GitLabReleaseProvider : UnsupportedReleaseProvider
{
    protected override string HostLabel => "GitLab";
}

/// <summary>Bitbucket releases provider stub (T-28).</summary>
internal sealed class BitbucketReleaseProvider : UnsupportedReleaseProvider
{
    protected override string HostLabel => "Bitbucket";
}

/// <summary>Azure DevOps releases provider stub (T-28).</summary>
internal sealed class AzureDevOpsReleaseProvider : UnsupportedReleaseProvider
{
    protected override string HostLabel => "Azure DevOps";
}
