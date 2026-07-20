using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic "why behind a commit" service (T-32). Resolves the repo's origin host + stored token,
/// parses <c>owner/repo</c> from the remote once (via the shared <see cref="HostConnectionResolver"/> —
/// the same path <see cref="IPullRequestService"/> and <see cref="IIssueService"/> use), and dispatches to
/// the matching provider (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs). Given a commit SHA it returns
/// the pull request(s) that contain it and the issue(s) those PRs reference. The ViewModel consumes only
/// this surface — host-specific JSON shapes never leak out of the provider (G-10).
///
/// <para>SECURITY (G-4): a token resolved here travels only in the provider's
/// <c>Authorization</c> header — never a URL query, argv, log, or exception message.</para>
/// </summary>
public interface ICommitContextService
{
    /// <summary>True when the repo's origin host has an implemented provider and a token is stored for it.</summary>
    bool IsSupported(string repoPath);

    /// <summary>The PR(s) that introduced/contain <paramref name="sha"/> plus the issues they reference.</summary>
    Task<CommitContextResult> GetForCommitAsync(string repoPath, string sha, CancellationToken ct);
}
