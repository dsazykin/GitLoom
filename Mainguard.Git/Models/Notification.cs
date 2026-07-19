using System;

namespace Mainguard.Git.Models;

/// <summary>
/// Why a notification landed in the authenticated user's inbox (T-27). Host <c>reason</c> strings are
/// mapped to exactly one of these by the pure <c>NotificationMapper</c>, so no host dialect ever reaches
/// the ViewModel or the reason chip. Anything unrecognized folds to <see cref="Other"/>.
/// </summary>
public enum NotificationReason
{
    /// <summary>The user was @-mentioned.</summary>
    Mention,

    /// <summary>The user's review was requested on a pull request.</summary>
    ReviewRequested,

    /// <summary>The user was assigned to the thread.</summary>
    Assign,

    /// <summary>The user opened the thread (author).</summary>
    Author,

    /// <summary>The user commented on the thread.</summary>
    Comment,

    /// <summary>The thread's state changed (e.g. closed/merged).</summary>
    StateChange,

    /// <summary>The user is subscribed to the thread.</summary>
    Subscribed,

    /// <summary>A team the user belongs to was mentioned.</summary>
    TeamMention,

    /// <summary>A CI / workflow run the user cares about changed state.</summary>
    CiActivity,

    /// <summary>Any reason not modeled above (invitation, manual, security alert, …).</summary>
    Other,
}

/// <summary>
/// What the notification points at (T-27). Host <c>subject.type</c> strings are mapped to exactly one of
/// these by the pure <c>NotificationMapper</c>, driving the per-item subject-kind icon.
/// </summary>
public enum NotificationSubjectKind
{
    PullRequest,
    Issue,
    Commit,
    Release,
    Discussion,

    /// <summary>Any subject type not modeled above (CheckSuite, RepositoryVulnerabilityAlert, …).</summary>
    Other,
}

/// <summary>
/// One notification thread in the authenticated user's inbox (T-27), normalized to the host-agnostic shape.
/// <see cref="Id"/> is the thread id used to mark the thread read; <see cref="Url"/> is a best-effort web
/// URL derived from the host's API subject URL (jump-to may be empty when the host gives no linkable URL).
/// </summary>
public sealed class NotificationItem
{
    /// <summary>Thread id (string; used for mark-read).</summary>
    public string Id { get; init; } = "";

    public NotificationReason Reason { get; init; }
    public NotificationSubjectKind Kind { get; init; }

    /// <summary>The subject title (e.g. the PR/issue title).</summary>
    public string Title { get; init; } = "";

    /// <summary>The <c>owner/repo</c> the thread belongs to (used to group the inbox).</summary>
    public string RepoFullName { get; init; } = "";

    /// <summary>A best-effort web URL for jump-to; empty when the host provides no linkable URL.</summary>
    public string Url { get; init; } = "";

    public bool Unread { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
