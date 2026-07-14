using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;

namespace GitLoom.Core.Hosting;

/// <summary>
/// Base for not-yet-implemented "list my repositories" providers (P2-48). The dispatch table stays
/// complete so wiring a real provider is additive, but the operation throws a typed "not yet supported
/// for &lt;host&gt;" and <see cref="IsImplemented"/> is false so <c>HostRepositoryService.IsSupported</c>
/// reports the host as unsupported (the Clone Dashboard then simply doesn't offer it as a provider) —
/// mirroring the T-23 <c>UnsupportedPullRequestProvider</c> pattern.
/// </summary>
internal abstract class UnsupportedRepositoryProvider : IHostRepositoryProvider
{
    protected abstract string HostLabel { get; }

    public bool IsImplemented => false;

    public Task<IReadOnlyList<RemoteRepository>> ListMyRepositoriesAsync(string host, string token, CancellationToken ct) =>
        throw new GitOperationException($"Listing repositories is not yet supported for {HostLabel}.");
}

/// <summary>Bitbucket repository-listing provider stub (P2-48): <c>/2.0/repositories</c> lands with the live matrix.</summary>
internal sealed class BitbucketRepositoryProvider : UnsupportedRepositoryProvider
{
    protected override string HostLabel => "Bitbucket";
}

/// <summary>Azure DevOps repository-listing provider stub (P2-48): <c>/_apis/git/repositories</c> lands with the live matrix.</summary>
internal sealed class AzureDevOpsRepositoryProvider : UnsupportedRepositoryProvider
{
    protected override string HostLabel => "Azure DevOps";
}
