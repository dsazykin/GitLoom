using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using GitLoom.Core.Services;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Notifications inbox (T-27), sibling of the Issues/PR/Checks panels: lists the authenticated user's
/// notifications for the repo's origin host, grouped by repository, each with a reason chip + subject-kind
/// icon + title + updated-at and unread styling. Per-item <b>mark read</b>, <b>mark all read</b>, <b>open</b>
/// (jump to the thread's URL) and an <b>Unread only</b> toggle. When the origin host is unsupported or no
/// token is stored it shows a graceful sign-in / unsupported affordance instead of erroring.
///
/// <para>All network work runs inside the async <see cref="INotificationService"/> (off the UI thread) and
/// is gated by <see cref="IsBusy"/>; the bound <see cref="Groups"/> collection is only ever mutated on the
/// <see cref="Dispatcher.UIThread"/>. Hosted by NotificationsWindow.</para>
/// </summary>
public partial class NotificationsViewModel : ViewModelBase
{
    private readonly INotificationService _notifications;
    private readonly string _repoPath;
    private readonly Action<string> _openUrl;
    private CancellationTokenSource? _cts;

    /// <summary>Notifications grouped by repository (owner/repo), newest thread first inside each group.</summary>
    public ObservableCollection<NotificationGroupViewModel> Groups { get; } = new();

    [ObservableProperty]
    private bool _isSupported;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True once a load has completed and there are no notifications to show under the current filter.</summary>
    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Shown when <see cref="IsSupported"/> is false — the unsupported-host / sign-in affordance text.</summary>
    [ObservableProperty]
    private string _unsupportedHint = "";

    /// <summary>When true, only unread notifications are listed. Flipping it reloads.</summary>
    [ObservableProperty]
    private bool _unreadOnly = true;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public NotificationsViewModel(INotificationService notifications, string repoPath, Action<string>? openUrl = null)
    {
        _notifications = notifications;
        _repoPath = repoPath;
        _openUrl = openUrl ?? Services.BrowserLauncher.OpenUrl;

        IsSupported = SafeIsSupported();
        if (!IsSupported)
        {
            UnsupportedHint =
                "Notifications aren't available for this repository yet. Connect an account for the origin host " +
                "(GitHub is supported today) from Accounts, then reopen this panel.";
        }
    }

    private bool SafeIsSupported()
    {
        try { return _notifications.IsSupported(_repoPath); }
        catch { return false; }
    }

    // ---- Filter ------------------------------------------------------------------------------

    partial void OnUnreadOnlyChanged(bool value)
    {
        if (IsSupported)
            _ = Refresh();
    }

    [RelayCommand]
    private void ShowUnreadOnly() { if (!UnreadOnly) UnreadOnly = true; }

    [RelayCommand]
    private void ShowAll() { if (UnreadOnly) UnreadOnly = false; }

    // ---- Load --------------------------------------------------------------------------------

    private bool CanRefresh => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task Refresh()
    {
        if (!IsSupported || IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var items = await _notifications.ListAsync(_repoPath, UnreadOnly, ct);
            await ApplyOnUiAsync(() =>
            {
                Groups.Clear();
                foreach (var g in items
                             .GroupBy(i => i.RepoFullName)
                             .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var group = new NotificationGroupViewModel(g.Key);
                    foreach (var item in g.OrderByDescending(i => i.UpdatedAt))
                        group.Items.Add(new NotificationRowViewModel(item, this));
                    Groups.Add(group);
                }
                IsEmpty = Groups.Count == 0;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh — ignore */ }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Mark read ---------------------------------------------------------------------------

    private bool CanMarkAllRead => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanMarkAllRead))]
    private async Task MarkAllRead()
    {
        if (!IsSupported || IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _notifications.MarkAllReadAsync(_repoPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        // Reflect the host result rather than guessing local state.
        if (ErrorMessage is null)
            await Refresh();
    }

    internal async Task MarkReadAsync(NotificationRowViewModel row)
    {
        if (IsBusy || !row.Unread) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _notifications.MarkReadAsync(_repoPath, row.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await Refresh();
    }

    internal void Open(NotificationRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.Url))
            _openUrl(row.Url);
    }

    // ---- Plumbing ----------------------------------------------------------------------------

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();
    partial void OnIsSupportedChanged(bool value) => NotifyCommandStates();

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        MarkAllReadCommand.NotifyCanExecuteChanged();
    }

    // Applies a mutation to bound state on the UI thread (invariant G-5): never mutates the observable
    // collection off-thread. Runs inline when already on the UI thread.
    private static Task ApplyOnUiAsync(Action apply)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            apply();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(apply).GetTask();
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One repository's bucket of notifications in the inbox (T-27): the <c>owner/repo</c> header plus
/// its threads. Purely a grouping container; all actions live on the rows.</summary>
public sealed class NotificationGroupViewModel
{
    public string RepoFullName { get; }
    public ObservableCollection<NotificationRowViewModel> Items { get; } = new();

    public NotificationGroupViewModel(string repoFullName)
        => RepoFullName = string.IsNullOrEmpty(repoFullName) ? "(unknown repository)" : repoFullName;

    public string CountText => Items.Count == 1 ? "1 notification" : $"{Items.Count} notifications";
}

/// <summary>
/// One notification row (T-27): reason chip + subject-kind icon + title + updated-at, unread styling, and
/// per-item mark-read / open routed back through the parent. The subject kind and reason are exposed as
/// mutually-exclusive booleans / display strings so the View picks a design-token icon and chip (no color
/// logic in the VM).
/// </summary>
public partial class NotificationRowViewModel : ViewModelBase
{
    private readonly NotificationsViewModel _parent;

    public string Id { get; }
    public NotificationReason Reason { get; }
    public NotificationSubjectKind Kind { get; }
    public string Title { get; }
    public string Url { get; }
    public bool Unread { get; }
    public DateTimeOffset UpdatedAt { get; }

    public NotificationRowViewModel(NotificationItem item, NotificationsViewModel parent)
    {
        _parent = parent;
        Id = item.Id;
        Reason = item.Reason;
        Kind = item.Kind;
        Title = string.IsNullOrEmpty(item.Title) ? "(no title)" : item.Title;
        Url = item.Url;
        Unread = item.Unread;
        UpdatedAt = item.UpdatedAt;
    }

    // ---- Reason chip -------------------------------------------------------------------------

    public string ReasonText => Reason switch
    {
        NotificationReason.Mention => "Mention",
        NotificationReason.ReviewRequested => "Review requested",
        NotificationReason.Assign => "Assigned",
        NotificationReason.Author => "Author",
        NotificationReason.Comment => "Comment",
        NotificationReason.StateChange => "State change",
        NotificationReason.Subscribed => "Subscribed",
        NotificationReason.TeamMention => "Team mention",
        NotificationReason.CiActivity => "CI",
        _ => "Update",
    };

    // ---- Subject-kind icon (mutually exclusive so the View selects a token-styled glyph) ------

    public bool IsPullRequest => Kind == NotificationSubjectKind.PullRequest;
    public bool IsIssue => Kind == NotificationSubjectKind.Issue;
    public bool IsCommit => Kind == NotificationSubjectKind.Commit;
    public bool IsRelease => Kind == NotificationSubjectKind.Release;
    public bool IsDiscussion => Kind == NotificationSubjectKind.Discussion;
    public bool IsOtherKind =>
        Kind is not (NotificationSubjectKind.PullRequest or NotificationSubjectKind.Issue
            or NotificationSubjectKind.Commit or NotificationSubjectKind.Release
            or NotificationSubjectKind.Discussion);

    public string KindText => Kind switch
    {
        NotificationSubjectKind.PullRequest => "Pull request",
        NotificationSubjectKind.Issue => "Issue",
        NotificationSubjectKind.Commit => "Commit",
        NotificationSubjectKind.Release => "Release",
        NotificationSubjectKind.Discussion => "Discussion",
        _ => "Notification",
    };

    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public string UpdatedText => UpdatedAt == default ? "" : $"updated {UpdatedAt.LocalDateTime:MMM d, HH:mm}";

    [RelayCommand]
    private Task MarkRead() => _parent.MarkReadAsync(this);

    [RelayCommand]
    private void Open() => _parent.Open(this);
}
