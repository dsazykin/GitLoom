using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;

namespace Mainguard.Git.Notifications;

/// <summary>
/// Base for not-yet-implemented notifications providers (T-27). The dispatch table stays complete so wiring
/// a real provider is additive, but every operation throws a typed "not yet supported for &lt;host&gt;" and
/// <see cref="IsImplemented"/> is false so <c>NotificationService.IsSupported</c> reports the host as
/// unsupported (the UI then shows the graceful unsupported affordance rather than erroring).
/// </summary>
internal abstract class UnsupportedNotificationProvider : INotificationProvider
{
    protected abstract string HostLabel { get; }

    public bool IsImplemented => false;

    private Exception NotSupported() =>
        new GitOperationException($"Notifications are not yet supported for {HostLabel}.");

    public Task<IReadOnlyList<NotificationItem>> ListAsync(string token, bool onlyUnread, CancellationToken ct) => throw NotSupported();
    public Task MarkReadAsync(string token, string threadId, CancellationToken ct) => throw NotSupported();
    public Task MarkAllReadAsync(string token, CancellationToken ct) => throw NotSupported();
}

/// <summary>GitLab todos provider stub (T-27): <c>/notifications</c>-equivalent lands with the live matrix.</summary>
internal sealed class GitLabNotificationProvider : UnsupportedNotificationProvider
{
    protected override string HostLabel => "GitLab";
}

/// <summary>Bitbucket notifications provider stub (T-27).</summary>
internal sealed class BitbucketNotificationProvider : UnsupportedNotificationProvider
{
    protected override string HostLabel => "Bitbucket";
}

/// <summary>Azure DevOps notifications provider stub (T-27).</summary>
internal sealed class AzureDevOpsNotificationProvider : UnsupportedNotificationProvider
{
    protected override string HostLabel => "Azure DevOps";
}
