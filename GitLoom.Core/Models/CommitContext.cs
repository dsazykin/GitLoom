using System;
using System.Collections.Generic;

namespace GitLoom.Core.Models;

/// <summary>
/// A reference to an issue extracted from a pull-request title/body (T-32): either a bare <c>#12</c>
/// (which resolves to the PR's own repository) or a cross-repo <c>owner/repo#7</c>. Host-agnostic —
/// produced by the pure <c>IssueReferenceParser</c> and returned inside a <see cref="CommitContextResult"/>.
/// </summary>
public sealed class LinkedIssueRef
{
    /// <summary>The issue number (the digits after <c>#</c>).</summary>
    public int Number { get; init; }

    /// <summary><c>owner/repo</c> the issue lives in — the PR's repo for a bare <c>#n</c>, or the explicit
    /// <c>owner/repo</c> for a cross-repo <c>owner/repo#n</c>. May be empty when no default repo is known.</summary>
    public string RepoFullName { get; init; } = "";
}

/// <summary>
/// The "why" behind a blame line (T-32): the pull request(s) that introduced/contain a commit and the
/// issue(s) those PRs reference. Host-agnostic projection produced by an <c>ICommitContextProvider</c>;
/// the ViewModel never sees a host's JSON shape (G-10).
/// </summary>
public sealed class CommitContextResult
{
    /// <summary>The full commit SHA this context was resolved for.</summary>
    public string Sha { get; init; } = "";

    /// <summary>The pull requests that introduced/contain the commit (usually one; several after re-merges).</summary>
    public IReadOnlyList<PullRequestItem> PullRequests { get; init; } = Array.Empty<PullRequestItem>();

    /// <summary>The issues parsed from the PR bodies/titles (deduped by repo + number).</summary>
    public IReadOnlyList<LinkedIssueRef> LinkedIssues { get; init; } = Array.Empty<LinkedIssueRef>();
}
