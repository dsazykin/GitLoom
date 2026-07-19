using System;
using System.Collections.Generic;

namespace Mainguard.Git.Models;

/// <summary>
/// Lifecycle state of a pull/merge request (T-23), host-agnostic. <see cref="Draft"/> is
/// modelled as a distinct state for the list badge even though most hosts also expose a
/// separate draft flag (see <see cref="PullRequestItem.IsDraft"/>).
/// </summary>
public enum PullRequestState { Open, Closed, Merged, Draft }

/// <summary>How a merge is performed on the host (T-23): a normal merge commit, squash, or rebase.</summary>
public enum PullRequestMergeMethod { Merge, Squash, Rebase }

/// <summary>
/// A pull/merge request as shown in the list (T-23). Host-agnostic projection produced by an
/// <c>IPullRequestProvider</c>; the ViewModel never sees a host's JSON shape.
/// </summary>
public sealed class PullRequestItem
{
    public int Number { get; init; }
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string SourceBranch { get; init; } = "";   // head ref (friendly)
    public string TargetBranch { get; init; } = "";   // base ref (friendly)
    public PullRequestState State { get; init; }
    public bool IsDraft { get; init; }
    public string Url { get; init; } = "";            // web URL, for "open in browser"
}

/// <summary>Detailed view of a single pull request (T-23): body, mergeability, reviewers, checks.</summary>
public sealed class PullRequestDetail
{
    public PullRequestItem Summary { get; init; } = new();
    public string Body { get; init; } = "";
    public bool Mergeable { get; init; }
    public IReadOnlyList<string> Reviewers { get; init; } = Array.Empty<string>();
    public IReadOnlyList<(string Name, string State)> Checks { get; init; } = Array.Empty<(string, string)>();
}

/// <summary>The fields needed to open a pull request (T-23).</summary>
public sealed class CreatePullRequest
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public string SourceBranch { get; init; } = "";
    public string TargetBranch { get; init; } = "";
    public bool IsDraft { get; init; }
}
