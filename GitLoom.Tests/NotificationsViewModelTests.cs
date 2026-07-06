using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using GitLoom.App.ViewModels;
using GitLoom.Core.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-27 (ViewModel) — gating/state of <see cref="NotificationsViewModel"/> over fakes: the unsupported
/// host shows the affordance and disables actions, <see cref="NotificationsViewModel.IsBusy"/> gates every
/// command, the Unread-only toggle reloads with the right flag, the list groups by repository, and
/// mark-read / mark-all route through the service. No live network.
/// </summary>
public class NotificationsViewModelTests
{
    private static FakeNotificationService Svc(bool supported = true) =>
        new() { IsSupportedImpl = _ => supported };

    private static NotificationItem N(string id, string repo, NotificationReason reason = NotificationReason.Mention,
        NotificationSubjectKind kind = NotificationSubjectKind.Issue, bool unread = true, string url = "https://x/1",
        int day = 1) => new()
        {
            Id = id,
            RepoFullName = repo,
            Reason = reason,
            Kind = kind,
            Title = "t" + id,
            Unread = unread,
            Url = url,
            UpdatedAt = new DateTimeOffset(2026, 7, day, 10, 0, 0, TimeSpan.Zero),
        };

    [Fact]
    public void SupportedHost_EnablesRefreshAndMarkAll()
    {
        var vm = new NotificationsViewModel(Svc(), "/repo");

        Assert.True(vm.IsSupported);
        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.True(vm.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public void UnsupportedHost_ShowsAffordance_AndDisablesActions()
    {
        var vm = new NotificationsViewModel(Svc(supported: false), "/repo");

        Assert.False(vm.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnsupportedHint));
        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public void IsBusy_GatesAllCommands()
    {
        var vm = new NotificationsViewModel(Svc(), "/repo");
        Assert.True(vm.RefreshCommand.CanExecute(null));

        vm.IsBusy = true;

        Assert.False(vm.RefreshCommand.CanExecute(null));
        Assert.False(vm.MarkAllReadCommand.CanExecute(null));
    }

    [Fact]
    public void DefaultsToUnreadOnly()
    {
        var vm = new NotificationsViewModel(Svc(), "/repo");
        Assert.True(vm.UnreadOnly);
    }

    [AvaloniaFact]
    public async Task Refresh_GroupsByRepository_NewestFirst()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => new[]
        {
            N("1", "octocat/hello-world", day: 1),
            N("2", "danielsazykin/gitloom", day: 3),
            N("3", "octocat/hello-world", day: 5),
        };
        var vm = new NotificationsViewModel(svc, "/repo");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Groups.Count);
        Assert.False(vm.IsEmpty);
        // Groups are ordered by repo name (case-insensitive); danielsazykin < octocat.
        Assert.Equal("danielsazykin/gitloom", vm.Groups[0].RepoFullName);
        var octocat = vm.Groups.First(g => g.RepoFullName == "octocat/hello-world");
        Assert.Equal(2, octocat.Items.Count);
        // Newest-first inside the group: id 3 (day 5) precedes id 1 (day 1).
        Assert.Equal("3", octocat.Items[0].Id);
        Assert.Equal("1", octocat.Items[1].Id);
    }

    [AvaloniaFact]
    public async Task Refresh_EmptyList_SetsEmptyState()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<NotificationItem>();
        var vm = new NotificationsViewModel(svc, "/repo");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Groups);
        Assert.True(vm.IsEmpty);
    }

    [AvaloniaFact]
    public async Task UnreadOnlyToggle_Reloads_WithMatchingFlag()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<NotificationItem>();
        var vm = new NotificationsViewModel(svc, "/repo");

        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(true, svc.LastOnlyUnread);

        vm.UnreadOnly = false;      // flipping triggers a reload
        await FlushAsync();
        Assert.Equal(false, svc.LastOnlyUnread);

        vm.UnreadOnly = true;
        await FlushAsync();
        Assert.Equal(true, svc.LastOnlyUnread);
    }

    [AvaloniaFact]
    public async Task MarkRead_RoutesThreadIdThroughService_AndReloads()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<NotificationItem>();
        var vm = new NotificationsViewModel(svc, "/repo");
        var row = new NotificationRowViewModel(N("42", "octocat/hello-world", unread: true), vm);

        await row.MarkReadCommand.ExecuteAsync(null);

        Assert.Equal("42", svc.LastMarkReadId);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task MarkRead_NoOpOnAlreadyReadRow()
    {
        var svc = Svc();
        var vm = new NotificationsViewModel(svc, "/repo");
        var row = new NotificationRowViewModel(N("7", "octocat/hello-world", unread: false), vm);

        await row.MarkReadCommand.ExecuteAsync(null);

        Assert.Null(svc.LastMarkReadId); // a read row never calls the service
    }

    [AvaloniaFact]
    public async Task MarkAllRead_RoutesThroughService()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<NotificationItem>();
        var vm = new NotificationsViewModel(svc, "/repo");

        await vm.MarkAllReadCommand.ExecuteAsync(null);

        Assert.Equal(1, svc.MarkAllReadCount);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public void Row_MapsReasonChip_AndSubjectKindFlags()
    {
        var vm = new NotificationsViewModel(Svc(), "/repo");
        var pr = new NotificationRowViewModel(
            N("1", "o/r", NotificationReason.ReviewRequested, NotificationSubjectKind.PullRequest), vm);

        Assert.Equal("Review requested", pr.ReasonText);
        Assert.True(pr.IsPullRequest);
        Assert.False(pr.IsIssue);
        Assert.True(pr.HasUrl);
    }

    private static async Task FlushAsync()
    {
        // Let the fire-and-forget reload triggered by the toggle change run to completion.
        await Task.Yield();
        await Task.Delay(20);
    }
}
