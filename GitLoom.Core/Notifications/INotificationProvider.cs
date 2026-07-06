using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Models;

namespace GitLoom.Core.Notifications;

/// <summary>
/// Per-host notifications adapter (T-27), the sibling of <c>ICheckProvider</c>/<c>IIssueProvider</c>. One
/// concrete provider per host family speaks the host's REST dialect and maps its JSON to host-agnostic
/// <see cref="NotificationItem"/>s. The token is supplied per call and lives only in the provider's
/// <c>Authorization</c> header — never a URL/argv/log/message. Notifications are user-scoped, so no
/// <c>owner/repo</c> slug is passed.
///
/// <para><see cref="IsImplemented"/> is false for the GitLab/Bitbucket/Azure DevOps stubs so the dispatch
/// table is complete (adding a real provider is additive) while the service reports those hosts as
/// unsupported until their live flow lands.</para>
/// </summary>
internal interface INotificationProvider
{
    /// <summary>False for stub providers whose live flow isn't built yet; the service treats them as unsupported.</summary>
    bool IsImplemented { get; }

    Task<IReadOnlyList<NotificationItem>> ListAsync(string token, bool onlyUnread, CancellationToken ct);
    Task MarkReadAsync(string token, string threadId, CancellationToken ct);
    Task MarkAllReadAsync(string token, CancellationToken ct);
}
