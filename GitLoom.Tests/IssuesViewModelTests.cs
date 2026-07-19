using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using GitLoom.App.ViewModels;
using Mainguard.Git.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-24 (ViewModel) — gating/state of <see cref="IssuesViewModel"/> over fakes: New-issue is disabled
/// when the host is unsupported, <see cref="IssuesViewModel.IsBusy"/> gates every command, the Open/Closed
/// filter reloads with the right state, the list marshals onto the observable collection, and label chips
/// are built with a host-colored background + auto-contrast foreground. No live network.
/// </summary>
public class IssuesViewModelTests
{
    private static FakeIssueService Svc(bool supported = true) =>
        new() { IsSupportedImpl = _ => supported };

    private static IssueItem Item(int n, string title = "t", params (string name, string color)[] labels) => new()
    {
        Number = n,
        Title = title,
        Author = "danielsazykin",
        State = IssueState.Open,
        Labels = labels.Select(l => new IssueLabel { Name = l.name, Color = l.color }).ToList(),
    };

    [Fact]
    public void SupportedHost_EnablesListAndCreate()
    {
        var vm = new IssuesViewModel(Svc(), "/repo");

        Assert.True(vm.IsSupported);
        Assert.True(vm.RefreshListCommand.CanExecute(null));
        Assert.True(vm.BeginCreateCommand.CanExecute(null));
    }

    [Fact]
    public void UnsupportedHost_ShowsAffordance_AndDisablesActions()
    {
        var vm = new IssuesViewModel(Svc(supported: false), "/repo");

        Assert.False(vm.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnsupportedHint));
        Assert.False(vm.RefreshListCommand.CanExecute(null));
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
    }

    [Fact]
    public void IsBusy_GatesAllCommands()
    {
        var vm = new IssuesViewModel(Svc(), "/repo");
        Assert.True(vm.RefreshListCommand.CanExecute(null));

        vm.IsBusy = true;

        Assert.False(vm.RefreshListCommand.CanExecute(null));
        Assert.False(vm.BeginCreateCommand.CanExecute(null));
        Assert.False(vm.SubmitCreateCommand.CanExecute(null));
    }

    [Fact]
    public void SubmitCreate_DisabledWhenTitleBlank()
    {
        var vm = new IssuesViewModel(Svc(), "/repo") { NewTitle = "" };
        Assert.False(vm.SubmitCreateCommand.CanExecute(null));

        vm.NewTitle = "A real title";
        Assert.True(vm.SubmitCreateCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task RefreshList_MarshalsResultsIntoCollection_AndBuildsLabelChips()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => new[]
        {
            Item(101, "Bug", ("bug", "d73a4a"), ("priority: high", "b60205")),
            Item(100, "Feature"),
        };
        var vm = new IssuesViewModel(svc, "/repo");

        await vm.RefreshListCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Issues.Count);
        Assert.False(vm.IsEmpty);

        var bugRow = vm.Issues.First(r => r.Number == 101);
        Assert.Equal(2, bugRow.Labels.Count);
        Assert.True(bugRow.HasLabels);
        var chip = bugRow.Labels[0];
        Assert.Equal("bug", chip.Name);
        // Host hex painted onto the chip background; foreground is auto-contrast (white on this dark red).
        Assert.Equal(Color.FromRgb(0xd7, 0x3a, 0x4a), ((SolidColorBrush)chip.Background).Color);
        Assert.Equal(Colors.White, ((SolidColorBrush)chip.Foreground).Color);
    }

    [AvaloniaFact]
    public async Task LabelChip_LightColor_GetsDarkText()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => new[] { Item(1, "t", ("enhancement", "a2eeef")) }; // light cyan
        var vm = new IssuesViewModel(svc, "/repo");

        await vm.RefreshListCommand.ExecuteAsync(null);

        var chip = vm.Issues[0].Labels[0];
        Assert.Equal(Colors.Black, ((SolidColorBrush)chip.Foreground).Color);
    }

    [AvaloniaFact]
    public async Task OpenClosedFilter_Reloads_WithMatchingState()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<IssueItem>();
        var vm = new IssuesViewModel(svc, "/repo");

        await vm.RefreshListCommand.ExecuteAsync(null);
        Assert.Equal(IssueState.Open, svc.LastFilter);

        vm.ShowClosed = true;           // flipping the filter triggers a reload
        await FlushAsync();
        Assert.Equal(IssueState.Closed, svc.LastFilter);

        vm.ShowClosed = false;
        await FlushAsync();
        Assert.Equal(IssueState.Open, svc.LastFilter);
    }

    [AvaloniaFact]
    public async Task SubmitCreate_RoutesThroughService_ClearsForm_AndReloads()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<IssueItem>();
        var vm = new IssuesViewModel(svc, "/repo")
        {
            NewTitle = "New bug",
            NewLabels = "bug, ui",
            NewAssignees = "octocat",
        };
        vm.BeginCreateCommand.Execute(null);

        await vm.SubmitCreateCommand.ExecuteAsync(null);

        Assert.Equal(1, svc.CreateCount);
        Assert.False(vm.IsCreating);
        Assert.Equal("", vm.NewTitle);
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task ToggleState_RoutesThroughService_AndRefreshes()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<IssueItem>();
        var vm = new IssuesViewModel(svc, "/repo");
        var row = new IssueRowViewModel(new IssueItem { Number = 7, State = IssueState.Open }, vm);

        await row.ToggleStateCommand.ExecuteAsync(null);

        Assert.Equal((7, IssueState.Closed), svc.LastSetState); // open → close
        Assert.Null(vm.ErrorMessage);
    }

    [AvaloniaFact]
    public async Task Comment_RoutesThroughService_AndCollapsesBox()
    {
        var svc = Svc();
        svc.ListImpl = (_, _) => Array.Empty<IssueItem>();
        var vm = new IssuesViewModel(svc, "/repo");
        var row = new IssueRowViewModel(new IssueItem { Number = 9 }, vm) { CommentDraft = "Looks good", IsCommenting = true };

        await row.SubmitCommentCommand.ExecuteAsync(null);

        Assert.Equal((9, "Looks good"), svc.LastComment);
        Assert.False(row.IsCommenting);
        Assert.Equal("", row.CommentDraft);
    }

    private static async Task FlushAsync()
    {
        // Let the fire-and-forget reload triggered by the filter change run to completion.
        await Task.Yield();
        await Task.Delay(20);
    }
}
