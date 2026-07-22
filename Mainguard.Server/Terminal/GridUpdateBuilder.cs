using System;
using System.Collections.Generic;
using Mainguard.Agents.Terminal.Vterm;
using Mainguard.Protos.V1;

namespace Mainguard.Server.Terminal;

/// <summary>
/// Converts the engine-neutral vterm output (<see cref="VtermGridDelta"/> / <see cref="VtermGrid"/>)
/// into the wire <see cref="GridUpdate"/> protos. Row content is run-length encoded: consecutive
/// cells sharing (fg, bg, attrs, width) collapse into one <see cref="CellRun"/>, and cells the
/// application never wrote travel as a <c>blanks</c> count instead of glyph strings — which both
/// keeps steady-scroll traffic O(changed rows) and preserves the written-space vs positioned-gap
/// distinction the selection-copy contract depends on.
///
/// <para>Colour wire encoding (documented in the proto): high byte 0 = default, 1 = indexed with
/// the index in the low byte, 2 = RGB in the low 24 bits (r&lt;&lt;16 | g&lt;&lt;8 | b). The client
/// (<c>GridModel</c>) mirrors this decoding.</para>
/// </summary>
public static class GridUpdateBuilder
{
    /// <summary>Full-grid snapshot update (attach / post-resize / recovery).</summary>
    public static GridUpdate BuildSnapshot(VtermGrid grid)
    {
        var update = new GridUpdate
        {
            Cols = (uint)grid.Cols,
            Rows = (uint)grid.Rows,
            Snapshot = true,
            Cursor = new GridCursor
            {
                Row = (uint)grid.CursorRow,
                Col = (uint)grid.CursorCol,
                Visible = grid.CursorVisible,
            },
            Modes = BuildModes(grid.Modes),
        };

        for (var r = 0; r < grid.Rows; r++)
        {
            update.Damage.Add(BuildRow(r, grid.Cells[r]));
        }

        return update;
    }

    /// <summary>One coalesced tick as a delta update, or null when nothing changed.</summary>
    public static GridUpdate? BuildDelta(VtermGridDelta delta)
    {
        if (delta.IsEmpty)
        {
            return null;
        }

        var update = new GridUpdate
        {
            Cols = (uint)delta.Cols,
            Rows = (uint)delta.Rows,
            Snapshot = false,
            PushedTruncated = delta.PushedTruncated,
            Cursor = new GridCursor
            {
                Row = (uint)delta.CursorRow,
                Col = (uint)delta.CursorCol,
                Visible = delta.CursorVisible,
            },
        };

        foreach (var pushed in delta.PushedRows)
        {
            update.Pushed.Add(BuildRow(0, pushed)); // pushed rows are ring appends; row index unused
        }

        foreach (var op in delta.Ops)
        {
            update.Ops.Add(op switch
            {
                VtermGridOp.Scroll s => new GridOp
                {
                    Scroll = new ScrollRect
                    {
                        Top = (uint)Math.Max(0, s.Top),
                        Bottom = (uint)Math.Max(0, s.Bottom),
                        Left = (uint)Math.Max(0, s.Left),
                        Right = (uint)Math.Max(0, s.Right),
                        RowDelta = s.RowDelta,
                        ColDelta = s.ColDelta,
                    },
                },
                VtermGridOp.PopRows p => new GridOp { PopRows = (uint)p.Count },
                _ => throw new InvalidOperationException($"Unknown grid op {op.GetType().Name}."),
            });
        }

        foreach (var (row, cells) in delta.DamagedRows)
        {
            update.Damage.Add(BuildRow(row, cells));
        }

        if (delta.ModesChanged)
        {
            update.Modes = BuildModes(delta.Modes);
        }

        return update;
    }

    /// <summary>A full row as style runs (also serves the scrollback RPC, where
    /// <paramref name="rowIndex"/> is the absolute scrollback index). Same-style width-1 cells
    /// whose glyph is one BMP char collapse into the <c>packed</c> string form — one UTF-16 char
    /// per cell — so steady-scroll traffic stays near the content size instead of paying per-glyph
    /// protobuf string overhead.</summary>
    public static GridRow BuildRow(long rowIndex, IReadOnlyList<VtermCell> cells)
    {
        var row = new GridRow { Row = (uint)rowIndex };
        CellRun? run = null;
        var runKind = RunKind.Blank;
        System.Text.StringBuilder? packed = null;

        void FlushPacked()
        {
            if (run is not null && runKind == RunKind.Packed && packed is not null)
            {
                run.Packed = packed.ToString();
                packed = null;
            }
        }

        foreach (var cell in cells)
        {
            if (cell.Width == 0)
            {
                continue; // wide-glyph spacer — implied by the width-2 run
            }

            var kind = !cell.HasContent ? RunKind.Blank
                : cell.Width == 1 && IsPackable(cell.Text) ? RunKind.Packed
                : RunKind.Glyphs;
            var fg = EncodeColor(cell.Fg);
            var bg = EncodeColor(cell.Bg);
            var attrs = (uint)cell.Attrs;
            var width = (uint)cell.Width;

            var compatible = run is not null
                && runKind == kind
                && run.Fg == fg && run.Bg == bg && run.Attrs == attrs
                && (kind != RunKind.Glyphs || run.Width == width);

            if (!compatible)
            {
                FlushPacked();
                run = new CellRun { Fg = fg, Bg = bg, Attrs = attrs, Width = kind == RunKind.Glyphs ? width : 1u };
                runKind = kind;
                row.Runs.Add(run);
                if (kind == RunKind.Packed)
                {
                    packed = new System.Text.StringBuilder();
                }
            }

            switch (kind)
            {
                case RunKind.Blank:
                    run!.Blanks++;
                    break;
                case RunKind.Packed:
                    packed!.Append(cell.Text.Length > 0 ? cell.Text[0] : ' ');
                    break;
                default:
                    run!.Glyphs.Add(cell.Text.Length > 0 ? cell.Text : " ");
                    break;
            }
        }

        FlushPacked();
        return row;
    }

    private enum RunKind
    {
        Blank,
        Packed,
        Glyphs,
    }

    /// <summary>One UTF-16 char, not a surrogate — the packed form's one-char-per-cell contract.</summary>
    private static bool IsPackable(string text) =>
        text.Length == 1 && !char.IsSurrogate(text[0]);

    private static GridModes BuildModes(VtermModes modes) => new()
    {
        AltScreen = modes.AltScreen,
        BracketedPaste = modes.BracketedPaste,
        CursorKeysApp = modes.CursorKeysApplication,
        Mouse = (uint)Math.Max(0, modes.MouseMode),
        MouseSgr = modes.MouseSgr,
    };

    /// <summary>Wire colour encoding (see class remarks).</summary>
    public static uint EncodeColor(VtermColor color) => color.Kind switch
    {
        VtermColorKind.Indexed => (1u << 24) | color.Index,
        VtermColorKind.Rgb => (2u << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B,
        _ => 0u,
    };
}
