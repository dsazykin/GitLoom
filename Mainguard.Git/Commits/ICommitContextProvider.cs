using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;
using Mainguard.Git.Services; // shared RepoSlug (parsed once by HostConnectionResolver)

namespace Mainguard.Git.Commits;

/// <summary>
/// Per-host adapter (T-32) that resolves a commit's "why": the pull request(s) that contain it and the
/// issue(s) those PRs reference. One concrete provider per host family speaks the host's REST dialect and
/// maps its JSON to the host-agnostic <see cref="CommitContextResult"/>. The token is supplied per call
/// and lives only in the provider's <c>Authorization</c> header — never a URL/argv/log/message.
///
/// <para><see cref="IsImplemented"/> is false for the GitLab/Bitbucket/Azure DevOps stubs so the dispatch
/// table is complete (adding a real provider is additive) while the service reports those hosts as
/// unsupported until their live flow lands.</para>
/// </summary>
internal interface ICommitContextProvider
{
    /// <summary>False for stub providers whose live flow isn't built yet; the service treats them as unsupported.</summary>
    bool IsImplemented { get; }

    Task<CommitContextResult> GetForCommitAsync(RepoSlug repo, string token, string sha, CancellationToken ct);
}
