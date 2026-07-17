using System;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// The Settings-window "About / versions" surface (<see cref="VersionsViewModel"/>): the three
/// versions render from one <c>GetDaemonInfo</c> probe over the injectable query seam, and every
/// degraded daemon state (down, pre-RPC, unstamped payload) maps to honest text — never a throw,
/// never a blank.
/// </summary>
public class VersionsViewModelTests
{
    [Fact]
    public async Task ReachableDaemon_ShowsDaemonAndPayloadVersions_AndTheAppsOwn()
    {
        var vm = new VersionsViewModel(
            _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "0.1.1")),
            appVersion: "0.2.0+abc123");

        await vm.RefreshAsync();

        Assert.Equal("0.2.0+abc123", vm.AppVersion);
        Assert.Equal("0.2.0", vm.DaemonVersion);
        Assert.Equal("0.1.1", vm.OsVersion);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task UnreachableDaemon_SaysUnreachable_ForBothDaemonAndPayload_WithoutThrowing()
    {
        var vm = new VersionsViewModel(
            _ => throw new InvalidOperationException("connection refused"), appVersion: "0.2.0");

        await vm.RefreshAsync();

        Assert.Equal(VersionsViewModel.UnreachableText, vm.DaemonVersion);
        Assert.Equal(VersionsViewModel.UnreachableText, vm.OsVersion);
        Assert.False(vm.IsRefreshing);
    }

    [Fact]
    public async Task PreRpcDaemon_NullAnswer_SaysPreVersionReporting()
    {
        // null == the daemon answered Unimplemented — alive but too old to name itself.
        var vm = new VersionsViewModel(
            _ => Task.FromResult<DaemonVersionInfo?>(null), appVersion: "0.2.0");

        await vm.RefreshAsync();

        Assert.Equal(VersionsViewModel.PreRpcDaemonText, vm.DaemonVersion);
        Assert.Equal(VersionsViewModel.UnknownPayloadText, vm.OsVersion);
    }

    [Fact]
    public async Task UnstampedPayload_EmptyPayloadVersion_SaysNotStamped()
    {
        var vm = new VersionsViewModel(
            _ => Task.FromResult<DaemonVersionInfo?>(new DaemonVersionInfo("0.2.0", "")),
            appVersion: "0.2.0");

        await vm.RefreshAsync();

        Assert.Equal("0.2.0", vm.DaemonVersion);
        Assert.Equal(VersionsViewModel.UnstampedPayloadText, vm.OsVersion);
    }

    [Fact]
    public async Task Refresh_ReQueries_SoARestartedDaemonShowsItsNewVersion()
    {
        var calls = 0;
        var vm = new VersionsViewModel(
            _ => Task.FromResult<DaemonVersionInfo?>(
                ++calls == 1 ? new DaemonVersionInfo("0.1.0", "0.1.0") : new DaemonVersionInfo("0.2.0", "0.1.1")),
            appVersion: "0.2.0");

        await vm.RefreshAsync();
        Assert.Equal("0.1.0", vm.DaemonVersion);

        await vm.RefreshAsync();
        Assert.Equal("0.2.0", vm.DaemonVersion);
        Assert.Equal("0.1.1", vm.OsVersion);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void BeforeAnyFetch_TheSurfaceSaysChecking_NeverBlank()
    {
        var vm = new VersionsViewModel(
            _ => Task.FromResult<DaemonVersionInfo?>(null), appVersion: "0.2.0");

        Assert.Equal(VersionsViewModel.CheckingText, vm.DaemonVersion);
        Assert.Equal(VersionsViewModel.CheckingText, vm.OsVersion);
        Assert.False(string.IsNullOrWhiteSpace(vm.AppVersion));
    }

    [Fact]
    public async Task ConcurrentRefresh_IsCoalesced_TheSecondCallIsANoOp()
    {
        var gate = new TaskCompletionSource<DaemonVersionInfo?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var vm = new VersionsViewModel(
            _ =>
            {
                Interlocked.Increment(ref calls);
                return gate.Task;
            },
            appVersion: "0.2.0");

        var first = vm.RefreshAsync();
        var second = vm.RefreshAsync(); // no-op while the first is in flight
        gate.SetResult(new DaemonVersionInfo("0.2.0", "0.1.1"));
        await first;
        await second;

        Assert.Equal(1, calls);
        Assert.Equal("0.2.0", vm.DaemonVersion);
    }
}
