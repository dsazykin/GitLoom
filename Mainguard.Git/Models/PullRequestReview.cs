namespace Mainguard.Git.Models;

/// <summary>
/// The verdict a user submits with a review (T-25). Maps to the GitHub review event
/// <c>COMMENT | APPROVE | REQUEST_CHANGES</c> in the provider.
/// </summary>
public enum ReviewVerdict { Comment, Approve, RequestChanges }

/// <summary>
/// The state of a submitted (or pending) review (T-25), host-agnostic. Maps from GitHub's review
/// <c>state</c> (<c>PENDING | COMMENTED | APPROVED | CHANGES_REQUESTED | DISMISSED</c>).
/// </summary>
public enum ReviewState { Pending, Commented, Approved, ChangesRequested, Dismissed }

/// <summary>
/// One submitted review on a pull request (T-25): who reviewed, their verdict, the review body, and
/// when. Host-agnostic projection produced by an <c>IPullRequestProvider</c>; the ViewModel never sees
/// a host's JSON shape.
/// </summary>
public sealed class PullRequestReview
{
    public long Id { get; init; }
    public string Author { get; init; } = "";
    public ReviewState State { get; init; }
    public string Body { get; init; } = "";
    public System.DateTimeOffset SubmittedAt { get; init; }
}

/// <summary>
/// One inline (file/line) review comment on a pull request (T-25). Grouped by <see cref="Path"/> into
/// threads for display. <see cref="Line"/> is the new-file line number, or <c>null</c> when the comment
/// is on an outdated diff (the hunk it referenced has since changed) — which still renders cleanly.
/// </summary>
public sealed class ReviewComment
{
    public long Id { get; init; }
    public string Author { get; init; } = "";
    public string Path { get; init; } = "";
    public int? Line { get; init; }          // new-file line; null when outdated
    public string DiffHunk { get; init; } = "";
    public string Body { get; init; } = "";
    public System.DateTimeOffset When { get; init; }
}

/// <summary>The fields needed to submit a review on a pull request (T-25): the verdict + an optional body.</summary>
public sealed class SubmitReview
{
    public ReviewVerdict Verdict { get; init; }
    public string Body { get; init; } = "";
}
