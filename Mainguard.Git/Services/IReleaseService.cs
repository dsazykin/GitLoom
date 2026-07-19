using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Git.Services;

/// <summary>
/// Host-agnostic releases operations (T-28). Resolves the repo's origin host + stored token and parses
/// owner/repo from the remote once (via the shared <see cref="HostConnectionResolver"/> — the same path
/// <see cref="IPullRequestService"/> / <see cref="IIssueService"/> use), and dispatches list/create to the
/// matching provider (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs). <see cref="GenerateNotes"/> is
/// <b>local-only</b> — it reads commits through <see cref="IGitService"/> and never touches the network.
///
/// <para>SECURITY (G-4): a token resolved here travels only in the provider's
/// <c>Authorization</c> header — never a URL query, argv, log, or exception message.</para>
/// </summary>
public interface IReleaseService
{
    /// <summary>True when the repo's origin host has an implemented provider and a token is stored for it.</summary>
    bool IsSupported(string repoPath);

    Task<IReadOnlyList<ReleaseItem>> ListAsync(string repoPath, CancellationToken ct);

    /// <summary>
    /// Local-only: generate grouped markdown notes from the commits between the previous release tag
    /// (the highest semver-ish tag reachable from the target, or the repo start when there is none) and
    /// <paramref name="targetCommitish"/>. No network; an empty history yields empty notes (no throw).
    /// </summary>
    string GenerateNotes(string repoPath, string newTag, string targetCommitish);

    Task<ReleaseItem> CreateAsync(string repoPath, CreateRelease request, CancellationToken ct);
}
