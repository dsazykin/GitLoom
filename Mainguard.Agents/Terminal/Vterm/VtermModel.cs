using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Mainguard.Agents.Terminal.Vterm;

/// <summary>Engine-neutral cell colour: terminal default, a 0–255 palette index, or 24-bit RGB.
/// This is the daemon-side twin of the harness/proto colour models — proto conversion lives in
/// <c>Mainguard.Server</c> (this assembly stays proto-free).</summary>
public enum VtermColorKind : byte
{
    Default = 0,
    Indexed = 1,
    Rgb = 2,
}

/// <summary>A resolved cell colour (see <see cref="VtermColorKind"/>).</summary>
public readonly record struct VtermColor(VtermColorKind Kind, byte Index, byte R, byte G, byte B)
{
    public static readonly VtermColor Default = new(VtermColorKind.Default, 0, 0, 0, 0);

    public static VtermColor Indexed(byte index) => new(VtermColorKind.Indexed, index, 0, 0, 0);

    public static VtermColor Rgb(byte r, byte g, byte b) => new(VtermColorKind.Rgb, 0, r, g, b);
}

/// <summary>Cell attribute bitset (the subset the product renders; matches the proto encoding).</summary>
[Flags]
public enum VtermCellAttrs : byte
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Underline = 4,
    Reverse = 8,
    Strike = 16,
}

/// <summary>
/// One resolved screen cell. <see cref="Text"/> is the grapheme (base char + combining marks);
/// empty with <see cref="HasContent"/> false for a cell the application never wrote (an erased /
/// cursor-positioned gap — selection-copy collapses runs of those to a single space, which is how
/// Ink layouts that position words with <c>ESC[nG</c> copy correctly). <see cref="Width"/> is 2 for
/// the lead cell of a double-width grapheme and 0 for its trailing spacer; 1 otherwise.
/// </summary>
public readonly record struct VtermCell(
    string Text,
    bool HasContent,
    VtermColor Fg,
    VtermColor Bg,
    VtermCellAttrs Attrs,
    byte Width)
{
    public static readonly VtermCell Blank = new(string.Empty, false, VtermColor.Default, VtermColor.Default, VtermCellAttrs.None, 1);
}

/// <summary>A deep snapshot of the visible grid + cursor + modes (the daemon-side comparison and
/// snapshot currency; rows × cols of <see cref="VtermCell"/>).</summary>
public sealed class VtermGrid
{
    public VtermGrid(int cols, int rows, VtermCell[][] cells, int cursorRow, int cursorCol, bool cursorVisible, VtermModes modes)
    {
        Cols = cols;
        Rows = rows;
        Cells = cells;
        CursorRow = cursorRow;
        CursorCol = cursorCol;
        CursorVisible = cursorVisible;
        Modes = modes;
    }

    public int Cols { get; }
    public int Rows { get; }
    public VtermCell[][] Cells { get; }
    public int CursorRow { get; }
    public int CursorCol { get; }
    public bool CursorVisible { get; }
    public VtermModes Modes { get; }

    /// <summary>The visible text of a row with trailing blanks trimmed (diagnostics/tests).</summary>
    public string RowText(int row)
    {
        var sb = new StringBuilder();
        foreach (var cell in Cells[row])
        {
            if (cell.Width == 0)
            {
                continue; // wide-glyph spacer
            }

            sb.Append(cell.HasContent && cell.Text.Length > 0 ? cell.Text : " ");
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>The terminal mode state a client needs for input encoding and rendering. Alt-screen and
/// mouse come from vterm props; bracketed paste / DECCKM / SGR-mouse from the DECSET tracker
/// (libvterm handles those modes internally without surfacing them).</summary>
public readonly record struct VtermModes(
    bool AltScreen,
    bool BracketedPaste,
    bool CursorKeysApplication,
    int MouseMode, // 0 none, 1 click, 2 drag, 3 move (VTERM_PROP_MOUSE values)
    bool MouseSgr);

/// <summary>An ordered structural op inside one drained tick.</summary>
public abstract record VtermGridOp
{
    /// <summary>vterm moverect: the content of <c>[Top,Bottom)×[Left,Right)</c> moved by the deltas.</summary>
    public sealed record Scroll(int Top, int Bottom, int Left, int Right, int RowDelta, int ColDelta) : VtermGridOp;

    /// <summary>N rows returned from scrollback to the top of the screen (screen grew / alt-screen exit).</summary>
    public sealed record PopRows(int Count) : VtermGridOp;
}

/// <summary>
/// One coalesced tick of screen change drained from <see cref="VtermSession"/>: rows pushed into
/// scrollback (with content, oldest first), ordered structural ops, the damaged row set, cursor,
/// and modes. The server converts this to a <c>GridUpdate</c> proto; the update stream plus the
/// snapshot path is the whole client contract.
/// </summary>
public sealed class VtermGridDelta
{
    public required int Cols { get; init; }
    public required int Rows { get; init; }
    public required IReadOnlyList<VtermCell[]> PushedRows { get; init; }
    public required bool PushedTruncated { get; init; }
    public required IReadOnlyList<VtermGridOp> Ops { get; init; }
    public required IReadOnlyList<(int Row, VtermCell[] Cells)> DamagedRows { get; init; }
    public required int CursorRow { get; init; }
    public required int CursorCol { get; init; }
    public required bool CursorVisible { get; init; }
    public required VtermModes Modes { get; init; }
    public required bool ModesChanged { get; init; }

    public bool IsEmpty =>
        PushedRows.Count == 0 && Ops.Count == 0 && DamagedRows.Count == 0 && !ModesChanged && !CursorMoved;

    /// <summary>Whether the cursor moved relative to the previously drained tick.</summary>
    public required bool CursorMoved { get; init; }

    /// <summary>Human-readable shape summary for perf logs (rows damaged / pushed / ops).</summary>
    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"damage={DamagedRows.Count} pushed={PushedRows.Count} ops={Ops.Count}");
}
