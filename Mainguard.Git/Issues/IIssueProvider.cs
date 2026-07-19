using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug (parsed once by HostConnectionResolver)

namespace Mainguard.Git.Issues;

/// <summary>
/// Per-host issue-tracking adapter (T-24), the sibling of <c>IPullRequestProvider</c>. One concrete
/// provider per host family speaks the host's REST dialect and maps its JSON to the host-agnostic
/// models. The token is supplied per call and lives only in the provider's <c>Authorization</c> header
/// — never a URL/argv/log/message.
///
/// <para><see cref="IsImplemented"/> is false for the GitLab/Bitbucket/Azure DevOps stubs so the dispatch
/// table is complete (adding a real provider is additive) while the service reports those hosts as
/// unsupported until their live flow lands.</para>
/// </summary>
internal interface IIssueProvider
{
    /// <summary>False for stub providers whose live flow isn't built yet; the service treats them as unsupported.</summary>
    bool IsImplemented { get; }

    Task<IReadOnlyList<IssueItem>> ListAsync(RepoSlug repo, string token, IssueState filter, CancellationToken ct);
    Task<IssueDetail> GetAsync(RepoSlug repo, string token, int number, CancellationToken ct);
    Task<IssueItem> CreateAsync(RepoSlug repo, string token, CreateIssue request, CancellationToken ct);
    Task<IssueComment> CommentAsync(RepoSlug repo, string token, int number, string body, CancellationToken ct);
    Task<IssueItem> SetStateAsync(RepoSlug repo, string token, int number, IssueState state, CancellationToken ct);
}
