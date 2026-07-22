using Mainguard.Agents.UI.Controls;
using Mainguard.Protos.V1;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// The REQUIRED P2-18 selection-copy contract (field promise 2026-07-22): drag → exact copied
/// text — glyph runs joined by single spaces where the app POSITIONED content across never-written
/// cells (the Ink <c>ESC[nG</c> layout), written spaces preserved verbatim, newlines between rows,
/// trailing blanks trimmed, wide-glyph spacers skipped — and a selection that lives in absolute
/// row space so damage-only redraws and live scrolling cannot detach it from its content.
/// </summary>
public sealed class GridSelectionTests
{
    [Fact]
    public void DragCopy_InkPositionedWords_JoinWithSingleSpaces()
    {
        // An Ink-style row: "Failed" ... gap ... "to" ... gap ... "connect" (words positioned with
        // cursor-column moves, the cells between never written).
        var model = new GridModel();
        var row = new GridRow { Row = 0 };
        row.Runs.Add(Run("Failed"));
        row.Runs.Add(new CellRun { Blanks = 4 });
        row.Runs.Add(Run("to"));
        row.Runs.Add(new CellRun { Blanks = 3 });
        row.Runs.Add(Run("connect"));
        model.ApplyGrid(GridModelTests.Snapshot(30, 2, row));

        var selection = new GridSelection();
        selection.Begin(0, 0); // absolute row space (ring empty ⇒ abs row 0 == screen row 0)
        selection.ExtendTo(0, 29);
        selection.EndDrag();

        Assert.Equal("Failed to connect", selection.ExtractText(model));
    }

    [Fact]
    public void DragCopy_MultiRow_NewlinesBetweenRows_TrailingBlanksTrimmed()
    {
        var model = new GridModel();
        var row0 = new GridRow { Row = 0 };
        row0.Runs.Add(Run("first line"));
        row0.Runs.Add(new CellRun { Blanks = 10 }); // trailing gap — must be trimmed
        var row1 = new GridRow { Row = 1 };
        row1.Runs.Add(Run("second"));
        model.ApplyGrid(GridModelTests.Snapshot(20, 2, row0, row1));

        var selection = new GridSelection();
        selection.Begin(0, 0);
        selection.ExtendTo(1, 19);
        selection.EndDrag();

        Assert.Equal("first line\nsecond", selection.ExtractText(model));
    }

    [Fact]
    public void DragCopy_WrittenSpacesArePreserved_GapsCollapse()
    {
        var model = new GridModel();
        var row = new GridRow { Row = 0 };
        row.Runs.Add(Run("a  b")); // double WRITTEN space — content, preserved
        row.Runs.Add(new CellRun { Blanks = 5 });
        row.Runs.Add(Run("c"));
        model.ApplyGrid(GridModelTests.Snapshot(20, 1, row));

        var selection = new GridSelection();
        selection.Begin(0, 0);
        selection.ExtendTo(0, 19);
        selection.EndDrag();

        Assert.Equal("a  b c", selection.ExtractText(model));
    }

    [Fact]
    public void DragCopy_WideGlyphs_SpacersDontDuplicate()
    {
        var model = new GridModel();
        var row = new GridRow { Row = 0 };
        row.Runs.Add(new CellRun { Glyphs = { "你", "好" }, Width = 2 });
        row.Runs.Add(new CellRun { Blanks = 1 });
        row.Runs.Add(Run("ok"));
        model.ApplyGrid(GridModelTests.Snapshot(20, 1, row));

        var selection = new GridSelection();
        selection.Begin(0, 0);
        selection.ExtendTo(0, 19);
        selection.EndDrag();

        Assert.Equal("你好 ok", selection.ExtractText(model));
    }

    [Fact]
    public void PartialRowSelection_RespectsColumnBounds()
    {
        var model = new GridModel();
        var row = new GridRow { Row = 0 };
        row.Runs.Add(Run("abcdef"));
        model.ApplyGrid(GridModelTests.Snapshot(20, 1, row));

        var selection = new GridSelection();
        selection.Begin(0, 2);
        selection.ExtendTo(0, 4);
        selection.EndDrag();

        Assert.Equal("cde", selection.ExtractText(model));
    }

    [Fact]
    public void BackwardDrag_NormalizesToReadingOrder()
    {
        var model = new GridModel();
        var row = new GridRow { Row = 0 };
        row.Runs.Add(Run("hello"));
        model.ApplyGrid(GridModelTests.Snapshot(20, 1, row));

        var selection = new GridSelection();
        selection.Begin(0, 4);
        selection.ExtendTo(0, 0);
        selection.EndDrag();

        Assert.Equal("hello", selection.ExtractText(model));
        Assert.True(selection.Contains(0, 2));
    }

    [Fact]
    public void Selection_SurvivesDamageOnlyRedraw_AndTracksScrolledContent()
    {
        // Select the top screen row, then let live output push a row into the ring: the selection
        // is anchored in absolute row space, so it still extracts the SAME content afterwards.
        var model = new GridModel();
        model.ApplyGrid(GridModelTests.Snapshot(10, 2, GridModelTests.Row(0, Run("keepme")), GridModelTests.Row(1, Run("live"))));

        var selection = new GridSelection();
        selection.Begin(0, 0);
        selection.ExtendTo(0, 9);
        selection.EndDrag();
        Assert.Equal("keepme", selection.ExtractText(model));

        // Damage-only redraw of row 1 — selection unaffected.
        model.ApplyGrid(GridModelTests.Delta(10, 2, damage: new[] { GridModelTests.Row(1, Run("redraw")) }));
        Assert.Equal("keepme", selection.ExtractText(model));

        // The selected row scrolls into the ring; absolute row 0 is now the ring row — same text.
        var scroll = GridModelTests.Delta(10, 2, damage: new[] { GridModelTests.Row(1, Run("newer")) });
        scroll.Pushed.Add(GridModelTests.Row(0, Run("keepme")));
        scroll.Ops.Add(new GridOp { Scroll = new ScrollRect { Top = 0, Bottom = 2, Left = 0, Right = 10, RowDelta = -1 } });
        model.ApplyGrid(scroll);

        Assert.Equal("keepme", selection.ExtractText(model));
    }

    [Fact]
    public void EmptySelection_ExtractsEmpty()
    {
        var model = new GridModel();
        model.ApplyGrid(GridModelTests.Snapshot(10, 1));
        Assert.Equal(string.Empty, new GridSelection().ExtractText(model));
    }

    private static CellRun Run(string text)
    {
        var run = new CellRun();
        foreach (var ch in text)
        {
            run.Glyphs.Add(ch.ToString());
        }

        return run;
    }
}
