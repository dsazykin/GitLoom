using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Models;

namespace GitLoom.Core.Services;

/// <summary>
/// Host-agnostic notifications-inbox operations (T-27), sibling of <see cref="ICheckStatusService"/> /
/// <see cref="IIssueService"/> / <see cref="IPullRequestService"/>. Notifications are the <b>authenticated
/// user's</b>, scoped by the token stored for the current repo's origin host. It resolves that host + token
/// once through the <b>shared</b> <see cref="HostConnectionResolver"/> (no duplicate host/token resolver),
/// then dispatches to the matching provider (GitHub v1; GitLab/Bitbucket/Azure DevOps stubs). The ViewModel
/// consumes only this surface — host-specific JSON never leaks past the provider.
///
/// <para>SECURITY (G-4): a token resolved here travels only in the provider's <c>Authorization</c> header —
/// never a URL query, argv, log, or exception message.</para>
/// </summary>
public interface INotificationService
{
    /// <summary>True when the repo's origin host has an implemented provider and a token is stored for it
    /// (notifications are user-scoped, so a token is required).</summary>
    bool IsSupported(string repoPath);

    /// <summary>The authenticated user's notifications; <paramref name="onlyUnread"/> restricts to unread.</summary>
    Task<IReadOnlyList<NotificationItem>> ListAsync(string repoPath, bool onlyUnread, CancellationToken ct);

    /// <summary>Marks a single notification thread as read by its id.</summary>
    Task MarkReadAsync(string repoPath, string threadId, CancellationToken ct);

    /// <summary>Marks every notification as read.</summary>
    Task MarkAllReadAsync(string repoPath, CancellationToken ct);
}
