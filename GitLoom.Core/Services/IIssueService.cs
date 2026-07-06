using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Host-agnostic issue-tracking operations (T-24). Resolves the repo's origin host + stored token,
/// parses owner/repo from the remote once (via the shared <see cref="HostConnectionResolver"/> — the
/// same path <see cref="IPullRequestService"/> uses), and dispatches to the matching provider (GitHub
/// v1; GitLab/Bitbucket/Azure DevOps stubs). The ViewModel consumes only this surface — host-specific
/// JSON shapes never leak out of the provider (G-10).
///
/// <para>SECURITY (G-4): a token resolved here travels only in the provider's
/// <c>Authorization</c> header — never a URL query, argv, log, or exception message.</para>
/// </summary>
public interface IIssueService
{
    /// <summary>True when the repo's origin host has an implemented provider and a token is stored for it.</summary>
    bool IsSupported(string repoPath);

    Task<IReadOnlyList<IssueItem>> ListAsync(string repoPath, IssueState filter, CancellationToken ct);
    Task<IssueDetail> GetAsync(string repoPath, int number, CancellationToken ct);
    Task<IssueItem> CreateAsync(string repoPath, CreateIssue request, CancellationToken ct);
    Task<IssueComment> CommentAsync(string repoPath, int number, string body, CancellationToken ct);
    Task<IssueItem> SetStateAsync(string repoPath, int number, IssueState state, CancellationToken ct); // close/reopen
}
