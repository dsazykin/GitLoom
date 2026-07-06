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
/// TI-26 (ViewModel) — gating/state of <see cref="ChecksViewModel"/> over the fake service: the overall
/// badge maps from the rolled-up <see cref="CommitChecks"/> and hides when the commit has no checks;
/// <see cref="ChecksViewModel.IsBusy"/> gates Refresh; the run list marshals onto the observable
/// collection; per-run Re-run routes through the service only for a re-requestable run; open-logs targets
/// the details URL; and an unsupported host shows the affordance instead of erroring. No live network.
/// </summary>
public class ChecksViewModelTests
{
    private static FakeCheckStatusService Svc(bool supported = true) => new() { IsSupportedImpl = _ => supported };

    private static CheckRunItem Run(long id, string name, CheckState state, string url = "") => new()
    {
        Id = id,
        Name = name,
        State = state,
        DetailsUrl = url,
    };

    private static CommitChecks Mixed() => CheckStateMapperRollup(new[]
    {
        Run(1, "build", CheckState.Success, "https://ci/1"),
        Run(2, "test", CheckState.Failure, "https://ci/2"),
        Run(3, "lint", CheckState.Pending, "https://ci/3"),
        Run(0, "legacy-deploy", CheckState.Success, "https://ci/legacy"), // legacy status: id 0 → not re-runnable
    });

    private static CommitChecks CheckStateMapperRollup(IReadOnlyList<CheckRunItem> runs) =>
        GitLoom.Core.Services.CheckStateMapper.Rollup("deadbeef", runs);

    [Fact]
    public void UnsupportedHost_ShowsAffordance_AndDisablesRefresh()
    {
        var vm = new ChecksViewModel(Svc(supported: false), "/repo", "deadbeef");

        Assert.False(vm.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnsupportedHint));
        Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void SupportedHost_EnablesRefresh()
    {
        var vm = new ChecksViewModel(Svc(), "/repo", "deadbeef");
        Assert.True(vm.IsSupported);
        Assert.True(vm.RefreshCommand.CanExecute(null));
    }

    [Fact]
    public void IsBusy_GatesRefresh()
    {
        var vm = new ChecksViewModel(Svc(), "/repo", "deadbeef");
        vm.IsBusy = true;
        Assert.False(vm.RefreshCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Refresh_PopulatesRuns_AndBadge_FromRollup()
    {
        var svc = Svc();
        svc.GetChecksImpl = (_, _) => Mixed();
        var vm = new ChecksViewModel(svc, "/repo", "deadbeef");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(4, vm.Runs.Count);
        Assert.True(vm.Badge.IsVisible);
        Assert.True(vm.Badge.IsFailure);        // a failing test dominates
        Assert.Equal(2, vm.Badge.Passed);
        Assert.Equal(1, vm.Badge.Failed);
        Assert.Equal(1, vm.Badge.Pending);
        Assert.False(vm.IsEmpty);
    }

    [AvaloniaFact]
    public async Task Refresh_NoChecks_HidesBadge_ShowsEmpty()
    {
        var svc = Svc();
        svc.GetChecksImpl = (_, sha) => CommitChecks.None(sha);
        var vm = new ChecksViewModel(svc, "/repo", "deadbeef");

        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Empty(vm.Runs);
        Assert.False(vm.Badge.IsVisible);
        Assert.True(vm.IsEmpty);
    }

    [AvaloniaFact]
    public async Task Rerun_OnlyForRerequestableRun_RoutesThroughService()
    {
        var svc = Svc();
        svc.GetChecksImpl = (_, _) => Mixed();
        var vm = new ChecksViewModel(svc, "/repo", "deadbeef");
        await vm.RefreshCommand.ExecuteAsync(null);

        var rerunnable = vm.Runs.First(r => r.Name == "test");
        var legacy = vm.Runs.First(r => r.Name == "legacy-deploy");

        Assert.True(rerunnable.CanRerun);
        Assert.False(legacy.CanRerun);   // legacy commit status (id 0) can't be re-run

        await rerunnable.RerunCommand.ExecuteAsync(null);
        Assert.Equal(1, svc.RerunCount);
        Assert.Equal(2, svc.LastRerunId);
    }

    [AvaloniaFact]
    public async Task Rerun_NonRerequestable_DoesNothing()
    {
        var svc = Svc();
        svc.GetChecksImpl = (_, _) => Mixed();
        var vm = new ChecksViewModel(svc, "/repo", "deadbeef");
        await vm.RefreshCommand.ExecuteAsync(null);

        var legacy = vm.Runs.First(r => r.Name == "legacy-deploy");
        await legacy.RerunCommand.ExecuteAsync(null);

        Assert.Equal(0, svc.RerunCount);
    }

    [AvaloniaFact]
    public async Task OpenLogs_OpensDetailsUrl()
    {
        var svc = Svc();
        svc.GetChecksImpl = (_, _) => Mixed();
        string? opened = null;
        var vm = new ChecksViewModel(svc, "/repo", "deadbeef", url => opened = url);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.Runs.First(r => r.Name == "build").OpenLogsCommand.Execute(null);
        Assert.Equal("https://ci/1", opened);
    }

    [Fact]
    public void Badge_Empty_IsHidden()
    {
        Assert.False(CheckBadgeViewModel.Empty.IsVisible);
        Assert.False(CheckBadgeViewModel.FromChecks(CommitChecks.None("x")).IsVisible);
    }
}
