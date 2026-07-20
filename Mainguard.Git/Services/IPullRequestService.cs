using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic pull/merge request operations (T-23). Resolves the repo's origin host + stored
/// token, parses owner/repo from the remote once, and dispatches to the matching provider
/// (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs). The ViewModel consumes only this surface —
/// host-specific JSON shapes never leak out of the provider (G-10).
///
/// <para>SECURITY (G-4): a token resolved here travels only in the provider's
/// <c>Authorization</c> header — never a URL query, argv, log, or exception message.</para>
/// </summary>
public interface IPullRequestService
{
    /// <summary>True when the repo's origin host has an implemented provider and a token is stored for it.</summary>
    bool IsSupported(string repoPath);

    Task<IReadOnlyList<PullRequestItem>> ListAsync(string repoPath, PullRequestState filter, CancellationToken ct);
    Task<PullRequestDetail> GetAsync(string repoPath, int number, CancellationToken ct);
    Task<PullRequestItem> CreateAsync(string repoPath, CreatePullRequest request, CancellationToken ct);
    Task<PullRequestItem> MergeAsync(string repoPath, int number, PullRequestMergeMethod method, CancellationToken ct);
    Task CloseAsync(string repoPath, int number, CancellationToken ct);

    // ---- Review (T-25) ------------------------------------------------------------------------

    /// <summary>The submitted reviews on a PR (verdict + author + body), newest as the host returns them.</summary>
    Task<IReadOnlyList<PullRequestReview>> GetReviewsAsync(string repoPath, int number, CancellationToken ct);

    /// <summary>The inline (file/line) review comments on a PR; the ViewModel groups them into threads by path.</summary>
    Task<IReadOnlyList<ReviewComment>> GetReviewCommentsAsync(string repoPath, int number, CancellationToken ct);

    /// <summary>Submits a review (approve / request-changes / comment + body) and returns the created review.</summary>
    Task<PullRequestReview> SubmitReviewAsync(string repoPath, int number, SubmitReview review, CancellationToken ct);
}
