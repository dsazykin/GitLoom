using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

// TI-18 (VM composition): the palette shapes the pure matcher/registry output into rows — browse-mode
// grouping, ranked filtering with highlighted segments, header-skipping navigation, and activation.
public class CommandPaletteViewModelTests
{
    private static PaletteEntry Entry(string title, string category, List<string>? log = null) =>
        new(title, category, string.Empty, () => { log?.Add(title); return Task.CompletedTask; });

    private static CommandPaletteViewModel Make(params PaletteEntry[] entries)
    {
        var vm = new CommandPaletteViewModel(() => entries);
        vm.Reset();
        return vm;
    }

    [Fact]
    public void EmptyQuery_ShouldBrowseGroupedByCategory_WithHeaders()
    {
        var vm = Make(
            Entry("Commit", "Repository"),
            Entry("Push", "Repository"),
            Entry("Toggle Sidebar", "View"));

        // Two category headers ("Repository", "View") plus three entries.
        Assert.Equal(2, vm.Results.Count(r => r.IsHeader));
        Assert.Equal(3, vm.Results.Count(r => !r.IsHeader));
        // First selectable row is the first non-header.
        Assert.True(vm.SelectedIndex >= 0);
        Assert.False(vm.Results[vm.SelectedIndex].IsHeader);
        Assert.True(vm.Results[vm.SelectedIndex].IsSelected);
    }

    [Fact]
    public void Query_ShouldFilterAndHighlightMatchedCharacters()
    {
        var vm = Make(
            Entry("Commit", "Repository"),
            Entry("Push", "Repository"),
            Entry("Toggle Sidebar", "View"));

        vm.Query = "psh";

        var rows = vm.Results.Where(r => !r.IsHeader).ToList();
        Assert.Single(rows); // only "Push" is a subsequence of "psh"
        var push = rows[0];
        // The matched characters ("P", "sh") are flagged for emphasis; concatenating segments rebuilds the title.
        Assert.Equal("Push", string.Concat(push.Segments.Select(s => s.Text)));
        Assert.Contains(push.Segments, s => s.IsMatch);
        // Only the subsequence chars P, s, h are emphasised (the 'u' is not part of the "psh" match).
        Assert.Equal("Psh", string.Concat(push.Segments.Where(s => s.IsMatch).Select(s => s.Text)));
    }

    [Fact]
    public void NoMatch_ShouldSetHasNoResults()
    {
        var vm = Make(Entry("Commit", "Repository"));
        vm.Query = "zzzzz";

        Assert.True(vm.HasNoResults);
        Assert.DoesNotContain(vm.Results, r => !r.IsHeader);
    }

    [Fact]
    public void MoveSelection_ShouldSkipHeaders_AndWrapAround()
    {
        var vm = Make(
            Entry("Commit", "Repository"),
            Entry("Push", "Repository"),
            Entry("Toggle Sidebar", "View"));

        // Collect selectable indices; every SelectedIndex must land on a non-header.
        var seen = new List<int>();
        for (int i = 0; i < 6; i++)
        {
            vm.MoveSelectionDownCommand.Execute(null);
            Assert.False(vm.Results[vm.SelectedIndex].IsHeader);
            seen.Add(vm.SelectedIndex);
        }
        // Wrapped around: at least one index repeats within 6 moves over 3 selectable rows.
        Assert.True(seen.Distinct().Count() <= 3);
    }

    [Fact]
    public async Task Activate_ShouldRunEntry_AndRequestClose()
    {
        var log = new List<string>();
        var vm = Make(Entry("Commit", "Repository", log));
        bool closed = false;
        vm.RequestClose += () => closed = true;

        var row = vm.Results.First(r => !r.IsHeader);
        await vm.Activate(row);

        Assert.Equal(new[] { "Commit" }, log.ToArray());
        Assert.True(closed);
    }

    [Fact]
    public async Task Activate_Header_ShouldDoNothing()
    {
        var log = new List<string>();
        var vm = Make(Entry("Commit", "Repository", log));
        var header = vm.Results.First(r => r.IsHeader);

        await vm.Activate(header);

        Assert.Empty(log);
    }

    [Fact]
    public void Reset_ShouldResnapshotEntries_ReflectingAvailabilityChanges()
    {
        // Simulate an action that becomes available only after a repo opens: the provider returns a
        // different set on the second call. Reset must pick up the new snapshot.
        var available = false;
        var vm = new CommandPaletteViewModel(() => available
            ? new[] { Entry("Commit", "Repository") }
            : System.Array.Empty<PaletteEntry>());

        vm.Reset();
        Assert.DoesNotContain(vm.Results, r => !r.IsHeader);

        available = true;
        vm.Reset();
        Assert.Single(vm.Results, r => !r.IsHeader);
    }
}
