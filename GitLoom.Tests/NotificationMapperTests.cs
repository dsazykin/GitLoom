using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-27 (pure) — pins every branch of the notification mapper so the reason chip and subject-kind icon can
/// never drift. Every GitHub <c>reason</c> and <c>subject.type</c> is mapped to its enum, and any
/// unrecognized/blank input folds deterministically to <c>Other</c>. Case/whitespace-insensitive.
/// </summary>
public class NotificationMapperTests
{
    // ---- reason → NotificationReason ----------------------------------------------------------

    [Theory]
    [InlineData("mention", NotificationReason.Mention)]
    [InlineData("review_requested", NotificationReason.ReviewRequested)]
    [InlineData("assign", NotificationReason.Assign)]
    [InlineData("author", NotificationReason.Author)]
    [InlineData("comment", NotificationReason.Comment)]
    [InlineData("state_change", NotificationReason.StateChange)]
    [InlineData("subscribed", NotificationReason.Subscribed)]
    [InlineData("team_mention", NotificationReason.TeamMention)]
    [InlineData("ci_activity", NotificationReason.CiActivity)]
    // Unmodeled GitHub reasons + junk → Other.
    [InlineData("invitation", NotificationReason.Other)]
    [InlineData("manual", NotificationReason.Other)]
    [InlineData("security_alert", NotificationReason.Other)]
    [InlineData("something_new", NotificationReason.Other)]
    [InlineData("", NotificationReason.Other)]
    [InlineData(null, NotificationReason.Other)]
    // Case/whitespace-insensitive.
    [InlineData("MENTION", NotificationReason.Mention)]
    [InlineData(" review_requested ", NotificationReason.ReviewRequested)]
    public void MapReason_IsPinned(string? reason, NotificationReason expected)
        => Assert.Equal(expected, NotificationMapper.MapReason(reason));

    // ---- subject.type → NotificationSubjectKind -----------------------------------------------

    [Theory]
    [InlineData("PullRequest", NotificationSubjectKind.PullRequest)]
    [InlineData("Issue", NotificationSubjectKind.Issue)]
    [InlineData("Commit", NotificationSubjectKind.Commit)]
    [InlineData("Release", NotificationSubjectKind.Release)]
    [InlineData("Discussion", NotificationSubjectKind.Discussion)]
    // Unmodeled GitHub subject types + junk → Other.
    [InlineData("CheckSuite", NotificationSubjectKind.Other)]
    [InlineData("RepositoryVulnerabilityAlert", NotificationSubjectKind.Other)]
    [InlineData("", NotificationSubjectKind.Other)]
    [InlineData(null, NotificationSubjectKind.Other)]
    // Case/whitespace-insensitive.
    [InlineData("pullrequest", NotificationSubjectKind.PullRequest)]
    [InlineData(" ISSUE ", NotificationSubjectKind.Issue)]
    public void MapSubjectKind_IsPinned(string? subjectType, NotificationSubjectKind expected)
        => Assert.Equal(expected, NotificationMapper.MapSubjectKind(subjectType));
}
