using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// The pure, unit-pinned T-27 mapper: turns a host notification's <c>reason</c> and <c>subject.type</c>
/// strings into the host-agnostic <see cref="NotificationReason"/> / <see cref="NotificationSubjectKind"/>
/// enums. It is the single tested place those host dialects are interpreted (mirrors <c>CheckStateMapper</c>);
/// anything unrecognized folds deterministically to <c>Other</c>. Case/whitespace-insensitive. No IO/host types.
/// </summary>
public static class NotificationMapper
{
    /// <summary>Maps a host notification <c>reason</c> to the enum; unknown/empty → <see cref="NotificationReason.Other"/>.</summary>
    public static NotificationReason MapReason(string? reason) => Norm(reason) switch
    {
        "mention" => NotificationReason.Mention,
        "review_requested" => NotificationReason.ReviewRequested,
        "assign" => NotificationReason.Assign,
        "author" => NotificationReason.Author,
        "comment" => NotificationReason.Comment,
        "state_change" => NotificationReason.StateChange,
        "subscribed" => NotificationReason.Subscribed,
        "team_mention" => NotificationReason.TeamMention,
        "ci_activity" => NotificationReason.CiActivity,
        _ => NotificationReason.Other,
    };

    /// <summary>Maps a host <c>subject.type</c> to the enum; unknown/empty → <see cref="NotificationSubjectKind.Other"/>.</summary>
    public static NotificationSubjectKind MapSubjectKind(string? subjectType) => Norm(subjectType) switch
    {
        "pullrequest" => NotificationSubjectKind.PullRequest,
        "issue" => NotificationSubjectKind.Issue,
        "commit" => NotificationSubjectKind.Commit,
        "release" => NotificationSubjectKind.Release,
        "discussion" => NotificationSubjectKind.Discussion,
        _ => NotificationSubjectKind.Other,
    };

    private static string Norm(string? s) => (s ?? "").Trim().ToLowerInvariant();
}
