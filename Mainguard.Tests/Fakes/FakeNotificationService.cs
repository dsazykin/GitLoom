using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace Mainguard.Tests.Fakes;

/// <summary>
/// Delegate-backed <see cref="INotificationService"/> fake (TI-27 VM tests), the sibling of
/// <see cref="FakeCheckStatusService"/>/<see cref="FakeIssueService"/>. Members a test uses are wired via
/// settable delegates; unstubbed members return benign defaults so a VM under test never has to configure
/// operations it doesn't exercise. <see cref="LastOnlyUnread"/>/<see cref="LastMarkReadId"/> capture calls
/// for assertions.
/// </summary>
public sealed class FakeNotificationService : INotificationService
{
    public Func<string, bool>? IsSupportedImpl { get; set; }
    public Func<string, bool, IReadOnlyList<NotificationItem>>? ListImpl { get; set; }
    public Action<string>? MarkReadImpl { get; set; }
    public Action? MarkAllReadImpl { get; set; }

    public bool? LastOnlyUnread { get; private set; }
    public string? LastMarkReadId { get; private set; }
    public int MarkAllReadCount { get; private set; }

    public bool IsSupported(string repoPath) => IsSupportedImpl?.Invoke(repoPath) ?? true;

    public Task<IReadOnlyList<NotificationItem>> ListAsync(string repoPath, bool onlyUnread, CancellationToken ct)
    {
        LastOnlyUnread = onlyUnread;
        return Task.FromResult(ListImpl?.Invoke(repoPath, onlyUnread) ?? Array.Empty<NotificationItem>());
    }

    public Task MarkReadAsync(string repoPath, string threadId, CancellationToken ct)
    {
        LastMarkReadId = threadId;
        MarkReadImpl?.Invoke(threadId);
        return Task.CompletedTask;
    }

    public Task MarkAllReadAsync(string repoPath, CancellationToken ct)
    {
        MarkAllReadCount++;
        MarkAllReadImpl?.Invoke();
        return Task.CompletedTask;
    }
}
