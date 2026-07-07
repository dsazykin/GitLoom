using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Host-agnostic CI / checks-status operations (T-26). Resolves the repo's origin host + stored token and
/// parses <c>owner/repo</c> once through the <b>shared</b> <see cref="HostConnectionResolver"/> (the same
/// path <see cref="IPullRequestService"/> and <see cref="IIssueService"/> use — no duplicate host/token
/// resolver), then dispatches to the matching provider (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs).
/// The ViewModel consumes only this surface — host-specific JSON shapes never leak past the provider.
///
/// <para>SECURITY (G-4): a token resolved here travels only in the provider's <c>Authorization</c>
/// header — never a URL query, argv, log, or exception message.</para>
/// </summary>
public interface ICheckStatusService
{
    /// <summary>True when the repo's origin host has an implemented provider and a token is stored for it.</summary>
    bool IsSupported(string repoPath);

    /// <summary>The merged check-run + legacy-status picture for a commit; a commit with no checks yields
    /// a result whose <see cref="CommitChecks.HasAny"/> is false (never throws for "no CI").</summary>
    Task<CommitChecks> GetChecksAsync(string repoPath, string sha, CancellationToken ct);

    /// <summary>Re-requests (re-runs) a single check-run by its numeric id.</summary>
    Task RerequestAsync(string repoPath, long checkRunId, CancellationToken ct);
}
