using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.Git.Models;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

// TI-00/TI-04: per-side accept/reject/undo resolution model on MergeChunkViewModel.
// MergeChunkViewModel is a plain ObservableObject, so these need no headless UI.
public class MergeChunkViewModelTests
{
    private static MergeChunkViewModel Conflict(string ours = "O", string theirs = "T")
        => new(new MergeChunk { Kind = ChunkKind.Conflict, LeftText = ours, RightText = theirs });

    [Fact]
    public void NonConflict_IsResolved_AndResultIsEffectiveText()
    {
        var vm = new MergeChunkViewModel(new MergeChunk { Kind = ChunkKind.LeftOnly, LeftText = "X", BaseText = "b" });
        Assert.True(vm.IsResolved);
        Assert.Equal("X", vm.ResultText);
    }

    [Fact]
    public void AcceptOne_ResolvesAndShowsThatSideLive_ThenBothAppends()
    {
        var vm = Conflict();
        vm.ToggleAcceptOurs();
        Assert.True(vm.IsResolved);           // accept-one is a resolution
        Assert.Equal("O", vm.ResultText);     // middle reflects the accepted side immediately
        vm.ToggleAcceptTheirs();
        Assert.Equal("O\nT", vm.ResultText);  // both accepted -> ours then theirs
    }

    [Fact]
    public void ToggleAccept_Twice_Undoes()
    {
        var vm = Conflict();
        vm.ToggleAcceptOurs();
        vm.ToggleAcceptOurs();                // undo
        Assert.Equal(SideChoice.Undecided, vm.OursChoice);
    }

    [Fact]
    public void AcceptOne_RejectOther_ResolvesToTheAcceptedSide()
    {
        var vm = Conflict();
        vm.ToggleRejectOurs();
        vm.ToggleAcceptTheirs();
        Assert.True(vm.IsResolved);
        Assert.Equal("T", vm.ResultText);
    }

    [Fact]
    public void RejectBoth_ResolvesToEmpty()
    {
        var vm = Conflict();
        vm.ToggleRejectOurs();
        vm.ToggleRejectTheirs();
        Assert.True(vm.IsResolved);
        Assert.Equal("", vm.ResultText);
    }

    [Fact]
    public void ForceOurs_And_ForceTheirs_Resolve()
    {
        var a = Conflict();
        a.ForceOurs();
        Assert.True(a.IsResolved);
        Assert.Equal("O", a.ResultText);

        var b = Conflict();
        b.ForceTheirs();
        Assert.True(b.IsResolved);
        Assert.Equal("T", b.ResultText);
    }

    [Fact]
    public void CustomEdit_ResolvesWithTypedText()
    {
        var vm = Conflict();
        vm.SetCustomFromEditor("merged by hand");
        Assert.True(vm.IsResolved);
        Assert.Equal("merged by hand", vm.ResultText);
    }
}
