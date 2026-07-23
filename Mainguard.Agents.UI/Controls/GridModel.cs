using System;
using System.Collections.Generic;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Controls;

/// <summary>One rendered grid cell client-side. <see cref="HasContent"/> distinguishes a WRITTEN
/// space (content, preserved by selection-copy) from an erased / cursor-positioned gap (collapsed
/// to a single space between runs — how Ink layouts copy correctly). <see cref="Width"/> is 2 for
/// a wide-glyph lead cell, 0 for its trailing spacer, 1 otherwise. Colours/attrs use the wire
/// encoding (<see cref="GridModel.ColorKind"/> / the proto attr bitset).</summary>
internal readonly record struct GridCellData(string Glyph, bool HasContent, uint Fg, uint Bg, byte Attrs, byte Width)
{
    public static readonly GridCellData Blank = new(string.Empty, false, 0, 0, 0, 1);
}

/// <summary>
/// The client half of the P2-18 grid engine: a pure, Avalonia-free mirror of the daemon's vterm
/// screen, built by applying <see cref="TerminalOutput"/> frames (snapshot → deltas) in arrival
/// order. It never reinterprets old frames at a new width — a size change only ever arrives as a
/// server snapshot (the one-authoritative-grid-size rule). Also maintains the local scrollback
/// ring fed by the update stream's pushed rows (capped; a firehose truncation is surfaced via
/// <see cref="ScrollbackGap"/>), the mode state the input encoder needs, and the OSC 52
/// <see cref="ClipboardCopyRequested"/> bridge (daemon-decoded; queries never reach the client).
///
/// <para>Deliberately mirrors <see cref="VtScreen"/>'s role for the interim engine: unit-testable
/// without a renderer, driven by <see cref="TerminalGridControl"/> behind <c>ITerminalView</c>.</para>
/// </summary>
internal sealed class GridModel
{
    public const int MaxScrollback = 10_000;

    private readonly LinkedList<GridCellData[]> _scrollback = new();
    private GridCellData[][] _screen;

    public GridModel(int cols = 80, int rows = 24)
    {
        Cols = Math.Max(1, cols);
        Rows = Math.Max(1, rows);
        _screen = NewScreen(Cols, Rows);
    }

    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AltScreen { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool CursorKeysApplication { get; private set; }

    /// <summary>0 none, 1 click, 2 drag, 3 move (mirrors VTERM_PROP_MOUSE).</summary>
    public int MouseMode { get; private set; }

    public bool MouseSgr { get; private set; }

    /// <summary>Whether the app is tracking the mouse — selection then requires the Shift override.</summary>
    public bool MouseTracking => MouseMode > 0;

    /// <summary>Lines in the local scrollback ring.</summary>
    public int ScrollbackCount => _scrollback.Count;

    /// <summary>True when a firehose tick truncated pushed rows — the local ring has a gap (the
    /// daemon ring stays complete; GetScrollback can backfill).</summary>
    public bool ScrollbackGap { get; private set; }

    /// <summary>True once the first snapshot arrived (the surface can render).</summary>
    public bool HasSnapshot { get; private set; }

    /// <summary>Raised after each applied frame; carries true when the applied frame changed the
    /// grid geometry (snapshot/resize) so the renderer can rebuild rather than patch.</summary>
    public event Action<bool>? Updated;

    /// <summary>The jailed CLI placed text on the clipboard via OSC 52 (decoded daemon-side).</summary>
    public event Action<string>? ClipboardCopyRequested;

    /// <summary>Back to the pristine pre-attach state at the current geometry: blank screen, empty
    /// scrollback ring, home cursor, every mode off. <see cref="HasSnapshot"/> drops too, so the
    /// surface renders as never-attached until a (for a dead session: never-coming) snapshot
    /// repaints it. Raises <see cref="Updated"/> with a geometry change so the renderer rebuilds.</summary>
    public void Reset()
    {
        _scrollback.Clear();
        _screen = NewScreen(Cols, Rows);
        CursorRow = 0;
        CursorCol = 0;
        CursorVisible = true;
        AltScreen = false;
        BracketedPaste = false;
        CursorKeysApplication = false;
        MouseMode = 0;
        MouseSgr = false;
        ScrollbackGap = false;
        HasSnapshot = false;
        Updated?.Invoke(true);
    }

    /// <summary>Applies one wire frame (a serialized <see cref="TerminalOutput"/> envelope).</summary>
    public void ApplyEnvelope(ReadOnlySpan<byte> data)
    {
        var output = TerminalOutput.Parser.ParseFrom(data.ToArray());
        Apply(output);
    }

    /// <summary>Applies one parsed output frame.</summary>
    public void Apply(TerminalOutput output)
    {
        switch (output.FrameCase)
        {
            case TerminalOutput.FrameOneofCase.Grid:
                ApplyGrid(output.Grid);
                break;
            case TerminalOutput.FrameOneofCase.Clipboard:
                ClipboardCopyRequested?.Invoke(output.Clipboard.Text);
                break;
        }
    }

    /// <summary>Applies one grid update. Fixed order: pushed rows → ring, structural ops, damage
    /// rows, cursor, modes (snapshot replaces the whole screen instead of ops+damage-patching).</summary>
    public void ApplyGrid(GridUpdate update)
    {
        var cols = (int)update.Cols;
        var rows = (int)update.Rows;
        var geometryChanged = update.Snapshot && (cols != Cols || rows != Rows || !HasSnapshot);

        foreach (var pushed in update.Pushed)
        {
            PushScrollback(DecodeRow(pushed, cols));
        }

        if (update.PushedTruncated)
        {
            ScrollbackGap = true;
        }

        foreach (var op in update.Ops)
        {
            switch (op.OpCase)
            {
                case GridOp.OpOneofCase.Scroll:
                    if (!update.Snapshot)
                    {
                        ApplyScroll(op.Scroll);
                    }

                    break;
                case GridOp.OpOneofCase.PopRows:
                    for (var i = 0; i < op.PopRows && _scrollback.Count > 0; i++)
                    {
                        _scrollback.RemoveLast();
                    }

                    break;
            }
        }

        if (update.Snapshot)
        {
            Cols = Math.Max(1, cols);
            Rows = Math.Max(1, rows);
            _screen = NewScreen(Cols, Rows);
            HasSnapshot = true;
        }

        foreach (var damaged in update.Damage)
        {
            var row = (int)damaged.Row;
            if (row >= 0 && row < Rows)
            {
                _screen[row] = DecodeRow(damaged, Cols);
            }
        }

        if (update.Cursor is not null)
        {
            CursorRow = Math.Clamp((int)update.Cursor.Row, 0, Rows - 1);
            CursorCol = Math.Clamp((int)update.Cursor.Col, 0, Cols - 1);
            CursorVisible = update.Cursor.Visible;
        }

        if (update.Modes is not null)
        {
            AltScreen = update.Modes.AltScreen;
            BracketedPaste = update.Modes.BracketedPaste;
            CursorKeysApplication = update.Modes.CursorKeysApp;
            MouseMode = (int)update.Modes.Mouse;
            MouseSgr = update.Modes.MouseSgr;
        }

        Updated?.Invoke(geometryChanged);
    }

    /// <summary>Total addressable rows: the scrollback ring then the live screen. Absolute row 0 is
    /// the oldest retained ring row; selection and the view offset use this space so both survive
    /// live scrolling.</summary>
    public int TotalRows => _scrollback.Count + Rows;

    /// <summary>A row by absolute index (ring rows first, then screen rows). Out of range → blank.</summary>
    public IReadOnlyList<GridCellData> GetAbsoluteRow(int absoluteRow)
    {
        if (absoluteRow >= _scrollback.Count)
        {
            var screenRow = absoluteRow - _scrollback.Count;
            return screenRow >= 0 && screenRow < Rows ? _screen[screenRow] : Array.Empty<GridCellData>();
        }

        if (absoluteRow < 0)
        {
            return Array.Empty<GridCellData>();
        }

        var node = _scrollback.First!;
        for (var i = 0; i < absoluteRow; i++)
        {
            node = node.Next!;
        }

        return node.Value;
    }

    /// <summary>The live screen row (renderer hot path — no ring walk).</summary>
    public GridCellData[] GetScreenRow(int row) => _screen[row];

    /// <summary>Trimmed text of a live screen row (tests/diagnostics).</summary>
    public string RowText(int row)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var cell in _screen[row])
        {
            if (cell.Width == 0)
            {
                continue;
            }

            sb.Append(cell.HasContent && cell.Glyph.Length > 0 ? cell.Glyph : " ");
        }

        return sb.ToString().TrimEnd();
    }

    // ---- wire decoding ----

    internal enum ColorKind
    {
        Default = 0,
        Indexed = 1,
        Rgb = 2,
    }

    internal static ColorKind KindOf(uint encoded) => (ColorKind)(encoded >> 24);

    internal static int IndexOf(uint encoded) => (int)(encoded & 0xFF);

    internal static (byte R, byte G, byte B) RgbOf(uint encoded) =>
        ((byte)(encoded >> 16), (byte)(encoded >> 8), (byte)encoded);

    private static GridCellData[] DecodeRow(GridRow row, int cols)
    {
        var cells = new GridCellData[Math.Max(1, cols)];
        Array.Fill(cells, GridCellData.Blank);
        var col = 0;
        foreach (var run in row.Runs)
        {
            var attrs = (byte)run.Attrs;
            var width = run.Width == 2 ? (byte)2 : (byte)1;
            if (run.Blanks > 0)
            {
                for (var i = 0; i < run.Blanks && col < cells.Length; i++)
                {
                    cells[col++] = new GridCellData(string.Empty, false, run.Fg, run.Bg, attrs, 1);
                }
            }

            foreach (var ch in run.Packed)
            {
                if (col >= cells.Length)
                {
                    break;
                }

                cells[col++] = new GridCellData(ch.ToString(), true, run.Fg, run.Bg, attrs, 1);
            }

            foreach (var glyph in run.Glyphs)
            {
                if (col >= cells.Length)
                {
                    break;
                }

                cells[col++] = new GridCellData(glyph, true, run.Fg, run.Bg, attrs, width);
                if (width == 2 && col < cells.Length)
                {
                    cells[col++] = new GridCellData(string.Empty, false, run.Fg, run.Bg, attrs, 0);
                }
            }
        }

        return cells;
    }

    private void ApplyScroll(ScrollRect scroll)
    {
        var top = (int)scroll.Top;
        var bottom = Math.Min((int)scroll.Bottom, Rows);
        var left = (int)scroll.Left;
        var right = Math.Min((int)scroll.Right, Cols);
        var dr = scroll.RowDelta;
        var dc = scroll.ColDelta;
        if (top >= bottom || left >= right || (dr == 0 && dc == 0))
        {
            return;
        }

        // The wire rect is dest ∪ src; the destination band excludes the |delta| rows/cols the
        // content vacated. Copy destination-first in the safe direction so overlap never smears.
        var destTop = dr > 0 ? top + dr : top;
        var destBottom = dr > 0 ? bottom : bottom + dr;
        var destLeft = dc > 0 ? left + dc : left;
        var destRight = dc > 0 ? right : right + dc;

        var rowsOrder = dr > 0
            ? RangeDescending(destBottom - 1, destTop)
            : RangeAscending(destTop, destBottom - 1);

        foreach (var r in rowsOrder)
        {
            var srcRow = r - dr;
            if (srcRow < 0 || srcRow >= Rows || r < 0 || r >= Rows)
            {
                continue;
            }

            if (dc == 0)
            {
                Array.Copy(_screen[srcRow], left, _screen[r], left, right - left);
                continue;
            }

            var colsOrder = dc > 0
                ? RangeDescending(destRight - 1, destLeft)
                : RangeAscending(destLeft, destRight - 1);
            foreach (var c in colsOrder)
            {
                var srcCol = c - dc;
                if (srcCol >= 0 && srcCol < Cols && c >= 0 && c < Cols)
                {
                    _screen[r][c] = _screen[srcRow][srcCol];
                }
            }
        }
    }

    private void PushScrollback(GridCellData[] row)
    {
        _scrollback.AddLast(row);
        while (_scrollback.Count > MaxScrollback)
        {
            _scrollback.RemoveFirst();
        }
    }

    private static IEnumerable<int> RangeAscending(int from, int to)
    {
        for (var i = from; i <= to; i++)
        {
            yield return i;
        }
    }

    private static IEnumerable<int> RangeDescending(int from, int to)
    {
        for (var i = from; i >= to; i--)
        {
            yield return i;
        }
    }

    private static GridCellData[][] NewScreen(int cols, int rows)
    {
        var screen = new GridCellData[rows][];
        for (var r = 0; r < rows; r++)
        {
            screen[r] = new GridCellData[cols];
            Array.Fill(screen[r], GridCellData.Blank);
        }

        return screen;
    }
}
