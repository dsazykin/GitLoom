using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using GitLoom.App.ViewModels;
using Mainguard.Git.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-28 (ViewModel) — gating/state of <see cref="ReleasesViewModel"/> over fakes: the composer is disabled
/// when the host is unsupported, <see cref="ReleasesViewModel.IsBusy"/> gates every command, Publish is
/// gated on a non-blank tag, Auto-generate-notes fills the body from the (local) service, and Publish routes
/// a <see cref="CreateRelease"/> with the right draft/prerelease fields. No live network.
/// </summary>
public class ReleasesViewModelTests
{
    private static FakeGitService Git() => new()
    {
        GetBranchesImpl = _ => new[] { new GitBranchItem { FriendlyName = "main", IsCurrentRepositoryHead = true } },
    };

    private static FakeReleaseService Svc(bool supported = true) =>
        new() { IsSupportedImpl = _ => supported };

    private static ReleasesViewModel Vm(FakeReleaseService svc) =>
        new(svc, Git(), "/repo");

    private static ReleaseItem Rel(string tag, bool draft = false, bool pre = false) => new()
    {
        TagName = tag,
        Name = tag,
        IsDraft = draft,
        IsPrerelease = pre,
        Url = $"https://github.com/o/r/releases/tag/{tag}",
    };

    [Fact]
    public void SupportedHost_EnablesListAndCompose()
    {
        var vm = Vm(Svc());
        Assert.True(vm.IsSupported);
        Assert.True(vm.RefreshListCommand.CanExecute(null));
        Assert.True(vm.BeginComposeCommand.CanExecute(null));
    }

    [Fact]
    public void UnsupportedHost_ShowsAffordance_AndDisablesActions()
    {
        var vm = Vm(Svc(supported: false));
        Assert.False(vm.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnsupportedHint));
        Assert.False(vm.RefreshListCommand.CanExecute(null));
        Assert.False(vm.BeginComposeCommand.CanExecute(null));
        Assert.False(vm.PublishCommand.CanExecute(null));
    }

    [Fact]
    public void Publish_Gated_OnNonBlankTag()
    {
        var vm = Vm(Svc());
        Assert.False(vm.PublishCommand.CanExecute(null)); // blank tag
        vm.NewTagName = "v1.0.0";
        Assert.True(vm.PublishCommand.CanExecute(null));
    }

    [Fact]
    public void IsBusy_GatesEveryCommand()
    {
        var vm = Vm(Svc());
        vm.NewTagName = "v1.0.0";
        vm.IsBusy = true;
        Assert.False(vm.RefreshListCommand.CanExecute(null));
        Assert.False(vm.BeginComposeCommand.CanExecute(null));
        Assert.False(vm.GenerateNotesCommand.CanExecute(null));
        Assert.False(vm.PublishCommand.CanExecute(null));
    }

    [Fact]
    public void PrefillsTargetFromCurrentBranch()
    {
        var vm = Vm(Svc());
        Assert.Equal("main", vm.NewTarget);
    }

    [Fact]
    public void SelectingExistingTag_FillsNewTagName()
    {
        var vm = Vm(Svc());
        vm.SelectedExistingTag = "v0.9.0";
        Assert.Equal("v0.9.0", vm.NewTagName);
        Assert.True(vm.PublishCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task AutoGenerateNotes_FillsBody_FromService()
    {
        var svc = Svc();
        svc.GenerateNotesImpl = (_, tag, target) => $"### Features\n- x (abc1234)\n\n**Full changelog:** {tag}";
        var vm = Vm(svc);
        vm.NewTagName = "v1.2.0";

        await vm.GenerateNotesCommand.ExecuteAsync(null);

        Assert.Contains("### Features", vm.NewBody);
        Assert.Equal(("v1.2.0", "main"), svc.LastGenerate);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task Publish_RoutesCreateRelease_WithDraftAndPrerelease()
    {
        var svc = Svc();
        svc.ListImpl = _ => Array.Empty<ReleaseItem>();
        var vm = Vm(svc);
        vm.NewTagName = "v2.0.0";
        vm.NewName = "Two point oh";
        vm.NewBody = "notes";
        vm.NewIsDraft = true;
        vm.NewIsPrerelease = true;

        await vm.PublishCommand.ExecuteAsync(null);

        Assert.Equal(1, svc.CreateCount);
        Assert.NotNull(svc.LastCreate);
        Assert.Equal("v2.0.0", svc.LastCreate!.TagName);
        Assert.Equal("Two point oh", svc.LastCreate.Name);
        Assert.Equal("main", svc.LastCreate.TargetCommitish);
        Assert.True(svc.LastCreate.IsDraft);
        Assert.True(svc.LastCreate.IsPrerelease);
        Assert.False(vm.IsComposing); // form closed on success
    }

    [AvaloniaFact]
    public async Task RefreshList_MarshalsResultsIntoCollection_WithBadges()
    {
        var svc = Svc();
        svc.ListImpl = _ => new[] { Rel("v1.0.0"), Rel("v1.1.0-rc1", pre: true), Rel("v1.2.0", draft: true) };
        var vm = Vm(svc);

        await vm.RefreshListCommand.ExecuteAsync(null);

        Assert.Equal(3, vm.Releases.Count);
        Assert.False(vm.IsEmpty);
        Assert.True(vm.Releases[1].IsPrerelease);
        Assert.True(vm.Releases[2].IsDraft);
        Assert.True(vm.Releases[0].HasUrl);
    }
}
