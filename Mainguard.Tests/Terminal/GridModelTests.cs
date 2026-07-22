using System.Collections.Generic;
using Google.Protobuf;
using Mainguard.Agents.UI.Controls;
using Mainguard.Protos.V1;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// The client half of the P2-18 grid engine (<see cref="GridModel"/>) — pure protobuf-in,
/// cells-out: snapshot replace, damage-row patching, first-class scroll ops (never a full-grid
/// resend), scrollback pushes/pops mirrored into the local ring, the daemon-authoritative modes,
/// and the daemon-decoded OSC 52 clipboard event.
/// </summary>
public sealed class GridModelTests
{
    [Fact]
    public void Snapshot_ReplacesGrid_AndSetsModes()
    {
        var model = new GridModel();
        model.ApplyGrid(Snapshot(10, 3, Row(0, Glyphs(0, "hi")), Row(1, Glyphs(0, "there"))));

        Assert.True(model.HasSnapshot);
        Assert.Equal(10, model.Cols);
        Assert.Equal(3, model.Rows);
        Assert.Equal("hi", model.RowText(0));
        Assert.Equal("there", model.RowText(1));
        Assert.True(model.BracketedPaste);
        Assert.Equal(2, model.MouseMode);
        Assert.True(model.MouseSgr);
        Assert.True(model.MouseTracking);
    }

    [Fact]
    public void Damage_PatchesOnlyNamedRows()
    {
        var model = new GridModel();
        model.ApplyGrid(Snapshot(10, 3, Row(0, Glyphs(0, "aaa")), Row(1, Glyphs(0, "bbb"))));

        model.ApplyGrid(Delta(10, 3, damage: new[] { Row(1, Glyphs(0, "XYZ")) }));

        Assert.Equal("aaa", model.RowText(0));
        Assert.Equal("XYZ", model.RowText(1));
    }

    [Fact]
    public void ScrollOp_MovesRowsWithoutFullGridSend()
    {
        var model = new GridModel();
        model.ApplyGrid(Snapshot(10, 3, Row(0, Glyphs(0, "r0")), Row(1, Glyphs(0, "r1")), Row(2, Glyphs(0, "r2"))));

        // One-line upward scroll (vterm moverect): union rect rows [0,3), delta -1, plus the newly
        // exposed bottom row as the only damage — the wire shape the perf invariant demands.
        var delta = Delta(10, 3, damage: new[] { Row(2, Glyphs(0, "r3")) });
        delta.Ops.Add(new GridOp
        {
            Scroll = new ScrollRect { Top = 0, Bottom = 3, Left = 0, Right = 10, RowDelta = -1 },
        });
        model.ApplyGrid(delta);

        Assert.Equal("r1", model.RowText(0));
        Assert.Equal("r2", model.RowText(1));
        Assert.Equal("r3", model.RowText(2));
    }

    [Fact]
    public void PushedRows_EnterTheLocalRing_AndPopsRemoveThem()
    {
        var model = new GridModel();
        model.ApplyGrid(Snapshot(10, 2, Row(0, Glyphs(0, "old")), Row(1, Glyphs(0, "new"))));

        var delta = Delta(10, 2, damage: new[] { Row(1, Glyphs(0, "live")) });
        delta.Pushed.Add(Row(0, Glyphs(0, "old")));
        model.ApplyGrid(delta);

        Assert.Equal(1, model.ScrollbackCount);
        Assert.Equal(3, model.TotalRows);
        Assert.Equal("old", RowTextOf(model.GetAbsoluteRow(0)));

        var pop = Delta(10, 2, damage: new[] { Row(0, Glyphs(0, "old")) });
        pop.Ops.Add(new GridOp { PopRows = 1 });
        model.ApplyGrid(pop);
        Assert.Equal(0, model.ScrollbackCount);
    }

    [Fact]
    public void PushedTruncated_SurfacesTheRingGap()
    {
        var model = new GridModel();
        model.ApplyGrid(Snapshot(10, 2));
        Assert.False(model.ScrollbackGap);

        var delta = Delta(10, 2, damage: new[] { Row(0, Glyphs(0, "x")) });
        delta.PushedTruncated = true;
        model.ApplyGrid(delta);
        Assert.True(model.ScrollbackGap);
    }

    [Fact]
    public void WrittenSpace_And_PositionedGap_StayDistinct()
    {
        var model = new GridModel();
        var row = new GridRow { Row = 0 };
        row.Runs.Add(new CellRun { Glyphs = { "a", " ", "b" } }); // written space = content
        row.Runs.Add(new CellRun { Blanks = 3 });                 // positioned gap = no content
        row.Runs.Add(new CellRun { Glyphs = { "c" } });
        model.ApplyGrid(Snapshot(10, 1, row));

        var cells = model.GetScreenRow(0);
        Assert.True(cells[1].HasContent);
        Assert.Equal(" ", cells[1].Glyph);
        Assert.False(cells[3].HasContent);
        Assert.True(cells[6].HasContent);
        Assert.Equal("c", cells[6].Glyph);
    }

    [Fact]
    public void WideGlyphRun_OccupiesLeadAndSpacerCells()
    {
        var model = new GridModel();
        var row = new GridRow { Row = 0 };
        row.Runs.Add(new CellRun { Glyphs = { "你", "好" }, Width = 2 });
        model.ApplyGrid(Snapshot(10, 1, row));

        var cells = model.GetScreenRow(0);
        Assert.Equal("你", cells[0].Glyph);
        Assert.Equal(2, cells[0].Width);
        Assert.Equal(0, cells[1].Width);
        Assert.Equal("好", cells[2].Glyph);
        Assert.Equal(0, cells[3].Width);
    }

    [Fact]
    public void ClipboardFrame_RaisesTheHostCopyEvent()
    {
        var model = new GridModel();
        var copies = new List<string>();
        model.ClipboardCopyRequested += copies.Add;

        model.Apply(new TerminalOutput { Clipboard = new ClipboardCopy { Text = "login-code-42" } });

        Assert.Equal(new[] { "login-code-42" }, copies);
    }

    [Fact]
    public void Envelope_RoundTripsThroughTheGatewayByteSeam()
    {
        var model = new GridModel();
        var output = new TerminalOutput { Grid = Snapshot(5, 2, Row(0, Glyphs(0, "ok"))) };

        model.ApplyEnvelope(output.ToByteArray());

        Assert.Equal("ok", model.RowText(0));
    }

    [Fact]
    public void Colors_DecodePerWireEncoding()
    {
        Assert.Equal(GridModel.ColorKind.Default, GridModel.KindOf(0));
        Assert.Equal(GridModel.ColorKind.Indexed, GridModel.KindOf((1u << 24) | 7));
        Assert.Equal(7, GridModel.IndexOf((1u << 24) | 7));
        Assert.Equal(GridModel.ColorKind.Rgb, GridModel.KindOf((2u << 24) | 0x0A141E));
        Assert.Equal(((byte)10, (byte)20, (byte)30), GridModel.RgbOf((2u << 24) | (10u << 16) | (20u << 8) | 30));
    }

    // ---- proto builders ----

    internal static GridUpdate Snapshot(int cols, int rows, params GridRow[] contentRows)
    {
        var update = new GridUpdate
        {
            Cols = (uint)cols,
            Rows = (uint)rows,
            Snapshot = true,
            Cursor = new GridCursor { Row = 0, Col = 0, Visible = true },
            Modes = new GridModes { BracketedPaste = true, Mouse = 2, MouseSgr = true },
        };
        update.Damage.AddRange(contentRows);
        return update;
    }

    internal static GridUpdate Delta(int cols, int rows, IReadOnlyList<GridRow>? damage = null)
    {
        var update = new GridUpdate
        {
            Cols = (uint)cols,
            Rows = (uint)rows,
            Snapshot = false,
            Cursor = new GridCursor { Row = 0, Col = 0, Visible = true },
        };
        if (damage is not null)
        {
            update.Damage.AddRange(damage);
        }

        return update;
    }

    internal static GridRow Row(int index, params CellRun[] runs)
    {
        var row = new GridRow { Row = (uint)index };
        row.Runs.AddRange(runs);
        return row;
    }

    internal static CellRun Glyphs(int leadingBlanks, string text)
    {
        var run = new CellRun();
        foreach (var ch in text)
        {
            run.Glyphs.Add(ch.ToString());
        }

        return leadingBlanks == 0 ? run : throw new System.ArgumentException("use a Blanks run instead");
    }

    private static string RowTextOf(IReadOnlyList<GridCellData> cells)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var cell in cells)
        {
            if (cell.Width == 0)
            {
                continue;
            }

            sb.Append(cell.HasContent && cell.Glyph.Length > 0 ? cell.Glyph : " ");
        }

        return sb.ToString().TrimEnd();
    }
}
