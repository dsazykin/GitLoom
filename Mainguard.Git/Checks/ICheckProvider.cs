using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug (parsed once by HostConnectionResolver)

namespace Mainguard.Git.Checks;

/// <summary>
/// Per-host CI/checks adapter (T-26), the sibling of <c>IPullRequestProvider</c>/<c>IIssueProvider</c>.
/// One concrete provider per host family speaks the host's REST dialect and maps its JSON to the
/// host-agnostic <see cref="CommitChecks"/>. The token is supplied per call and lives only in the
/// provider's <c>Authorization</c> header — never a URL/argv/log/message.
///
/// <para><see cref="IsImplemented"/> is false for the GitLab/Bitbucket/Azure DevOps stubs so the dispatch
/// table is complete (adding a real provider is additive) while the service reports those hosts as
/// unsupported until their live flow lands.</para>
/// </summary>
internal interface ICheckProvider
{
    /// <summary>False for stub providers whose live flow isn't built yet; the service treats them as unsupported.</summary>
    bool IsImplemented { get; }

    Task<CommitChecks> GetChecksAsync(RepoSlug repo, string token, string sha, CancellationToken ct);
    Task RerequestAsync(RepoSlug repo, string token, long checkRunId, CancellationToken ct);
}
