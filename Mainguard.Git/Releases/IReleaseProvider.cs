using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug (parsed once by HostConnectionResolver)

namespace Mainguard.Git.Releases;

/// <summary>
/// Per-host releases adapter (T-28), the sibling of <c>IIssueProvider</c>/<c>ICheckProvider</c>. One
/// concrete provider per host family speaks the host's REST dialect and maps its JSON to the
/// host-agnostic models. The token is supplied per call and lives only in the provider's
/// <c>Authorization</c> header — never a URL/argv/log/message.
///
/// <para><see cref="IsImplemented"/> is false for the GitLab/Bitbucket/Azure DevOps stubs so the dispatch
/// table is complete (adding a real provider is additive) while the service reports those hosts as
/// unsupported until their live flow lands.</para>
/// </summary>
internal interface IReleaseProvider
{
    /// <summary>False for stub providers whose live flow isn't built yet; the service treats them as unsupported.</summary>
    bool IsImplemented { get; }

    Task<IReadOnlyList<ReleaseItem>> ListAsync(RepoSlug repo, string token, CancellationToken ct);
    Task<ReleaseItem> CreateAsync(RepoSlug repo, string token, CreateRelease request, CancellationToken ct);
}
