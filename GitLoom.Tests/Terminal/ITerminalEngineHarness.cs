using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GitLoom.Tests.Terminal;

/// <summary>
/// P2-04 engine abstraction: the single "feed bytes → read grid" seam every VT engine is driven
/// through. The interim P2-03 renderer adapts to it via <see cref="InterimEngineHarness"/>; the
/// future P2-18 libvterm engine registers a second implementation and re-runs the same suites
/// unchanged. The abstraction is deliberately free of any Avalonia / vendored-renderer type
/// (coupling to those is a P2-04 rejection trigger) — it speaks only in <see cref="GridSnapshot"/>.
/// </summary>
public interface ITerminalEngineHarness
{
    /// <summary>A human-readable engine name for diff headers / test output.</summary>
    string EngineName { get; }

    /// <summary>Re-initialises the engine to a blank <paramref name="cols"/>×<paramref name="rows"/> grid.</summary>
    void Reset(int cols, int rows);

    /// <summary>Feeds a byte slice of terminal output into the engine's parser.</summary>
    void Feed(ReadOnlySpan<byte> bytes);

    /// <summary>Reads back the current visible grid + cursor + alt-screen flag as a snapshot.</summary>
    GridSnapshot ReadGrid();
}

/// <summary>Engine-agnostic cell colour: default, a 0–255 palette index, or exact 24-bit truecolor.</summary>
public readonly record struct CellColor
{
    public enum ColorKind
    {
        Default,
        Indexed,
        Rgb,
    }

    private CellColor(ColorKind kind, int index, byte r, byte g, byte b)
    {
        Kind = kind;
        Index = index;
        R = r;
        G = g;
        B = b;
    }

    public ColorKind Kind { get; }
    public int Index { get; }
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    public static readonly CellColor Default = new(ColorKind.Default, 0, 0, 0, 0);

    public static CellColor Indexed(int index) => new(ColorKind.Indexed, index, 0, 0, 0);

    public static CellColor Rgb(byte r, byte g, byte b) => new(ColorKind.Rgb, 0, r, g, b);

    /// <summary>Canonical golden token: <c>def</c>, <c>i&lt;n&gt;</c>, or <c>#rrggbb</c>.</summary>
    public override string ToString() => Kind switch
    {
        ColorKind.Indexed => "i" + Index.ToString(CultureInfo.InvariantCulture),
        ColorKind.Rgb => string.Create(CultureInfo.InvariantCulture, $"#{R:x2}{G:x2}{B:x2}"),
        _ => "def",
    };
}

/// <summary>Character attributes an engine may resolve onto a cell (interim tracks bold only).</summary>
[Flags]
public enum CellAttrs
{
    None = 0,
    Bold = 1 << 0,
    Italic = 1 << 1,
    Underline = 1 << 2,
    Reverse = 1 << 3,
}

/// <summary>
/// One resolved terminal cell. <see cref="Width"/> is 2 for the lead cell of a double-width
/// grapheme and 0 for its trailing spacer cell; 1 otherwise. <see cref="LinkUri"/> carries the
/// OSC 8 hyperlink target when present.
/// </summary>
public readonly record struct GridCell(
    string Text,
    CellColor Fg,
    CellColor Bg,
    CellAttrs Attrs,
    int Width,
    string? LinkUri)
{
    public static GridCell Blank => new(" ", CellColor.Default, CellColor.Default, CellAttrs.None, 1, null);

    public bool IsDefault =>
        Text == " " && Fg == CellColor.Default && Bg == CellColor.Default
        && Attrs == CellAttrs.None && Width == 1 && LinkUri is null;
}

/// <summary>
/// The comparison currency of the harness: an immutable, deep snapshot of the visible grid, cursor,
/// and alt-screen flag. Serialises to a deterministic, human-readable golden (LF-locked, no
/// timestamps, cell-addressed) and diffs cell-by-cell so a golden mismatch is diagnosable from CI
/// output alone.
/// </summary>
public sealed class GridSnapshot
{
    public GridSnapshot(int cols, int rows, GridCell[][] cells, int cursorRow, int cursorCol, bool altScreen)
    {
        Cols = cols;
        Rows = rows;
        Cells = cells;
        CursorRow = cursorRow;
        CursorCol = cursorCol;
        AltScreen = altScreen;
    }

    public int Cols { get; }
    public int Rows { get; }
    public GridCell[][] Cells { get; }
    public int CursorRow { get; }
    public int CursorCol { get; }
    public bool AltScreen { get; }

    /// <summary>Builds a blank grid (all default cells, cursor home).</summary>
    public static GridSnapshot Blank(int cols, int rows)
    {
        var cells = new GridCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            cells[r] = new GridCell[cols];
            for (var c = 0; c < cols; c++)
            {
                cells[r][c] = GridCell.Blank;
            }
        }

        return new GridSnapshot(cols, rows, cells, 0, 0, false);
    }

    /// <summary>Trimmed visible text of a row (test convenience).</summary>
    public string RowText(int row)
    {
        var sb = new StringBuilder();
        foreach (var cell in Cells[row])
        {
            sb.Append(string.IsNullOrEmpty(cell.Text) ? " " : cell.Text);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Canonical golden serialisation: a header (size, cursor, alt), one text line per row
    /// (trailing blanks trimmed), then a cell-addressed line for every cell carrying non-default
    /// colour / attrs / width / link. Deterministic and LF-terminated so regeneration is
    /// byte-identical.
    /// </summary>
    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("# gitloom-vt-golden v1\n");
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"size {Cols} {Rows}\n"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"cursor {CursorRow} {CursorCol}\n"));
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"alt {(AltScreen ? 1 : 0)}\n"));

        sb.Append("text\n");
        for (var r = 0; r < Rows; r++)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"r{r:000}|"));
            sb.Append(RowText(r));
            sb.Append('\n');
        }

        sb.Append("cells\n");
        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var cell = Cells[r][c];
                if (cell.IsDefault)
                {
                    continue;
                }

                sb.Append(string.Create(CultureInfo.InvariantCulture, $"r{r:000}c{c:000}"));
                if (cell.Fg != CellColor.Default)
                {
                    sb.Append(" fg=").Append(cell.Fg);
                }

                if (cell.Bg != CellColor.Default)
                {
                    sb.Append(" bg=").Append(cell.Bg);
                }

                if (cell.Attrs != CellAttrs.None)
                {
                    sb.Append(" a=").Append(AttrToken(cell.Attrs));
                }

                if (cell.Width != 1)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $" w={cell.Width}"));
                }

                if (cell.LinkUri is not null)
                {
                    sb.Append(" link=").Append(cell.LinkUri);
                }

                sb.Append('\n');
            }
        }

        sb.Append("end\n");
        return sb.ToString();
    }

    /// <summary>
    /// Cell-by-cell diff against <paramref name="other"/>. Returns an empty list when the grids are
    /// identical; otherwise each entry names the divergence (row/col/expected/actual).
    /// </summary>
    public IReadOnlyList<string> Diff(GridSnapshot other)
    {
        var diffs = new List<string>();

        if (Cols != other.Cols || Rows != other.Rows)
        {
            diffs.Add($"size: expected {Cols}x{Rows}, actual {other.Cols}x{other.Rows}");
            return diffs; // shapes differ — cell comparison would be meaningless
        }

        if (AltScreen != other.AltScreen)
        {
            diffs.Add($"alt-screen: expected {AltScreen}, actual {other.AltScreen}");
        }

        if (CursorRow != other.CursorRow || CursorCol != other.CursorCol)
        {
            diffs.Add($"cursor: expected ({CursorRow},{CursorCol}), actual ({other.CursorRow},{other.CursorCol})");
        }

        for (var r = 0; r < Rows; r++)
        {
            for (var c = 0; c < Cols; c++)
            {
                var e = Cells[r][c];
                var a = other.Cells[r][c];
                if (e != a)
                {
                    diffs.Add($"cell ({r},{c}): expected {Describe(e)}, actual {Describe(a)}");
                }
            }
        }

        return diffs;
    }

    /// <summary>True when the grids match cell-for-cell (plus size/cursor/alt).</summary>
    public bool GridEquals(GridSnapshot other) => Diff(other).Count == 0;

    /// <summary>A readable multi-line diff report, capped so a firehose mismatch stays legible.</summary>
    public string DiffReport(GridSnapshot other, int max = 40)
    {
        var diffs = Diff(other);
        if (diffs.Count == 0)
        {
            return "(identical)";
        }

        var sb = new StringBuilder();
        sb.Append(string.Create(CultureInfo.InvariantCulture, $"{diffs.Count} cell/grid difference(s):\n"));
        for (var i = 0; i < diffs.Count && i < max; i++)
        {
            sb.Append("  ").Append(diffs[i]).Append('\n');
        }

        if (diffs.Count > max)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"  … and {diffs.Count - max} more\n"));
        }

        return sb.ToString();
    }

    private static string Describe(GridCell cell)
    {
        var glyph = string.IsNullOrEmpty(cell.Text) ? "·" : cell.Text;
        var sb = new StringBuilder();
        sb.Append('\'').Append(glyph).Append('\'');
        if (cell.Fg != CellColor.Default)
        {
            sb.Append(" fg=").Append(cell.Fg);
        }

        if (cell.Bg != CellColor.Default)
        {
            sb.Append(" bg=").Append(cell.Bg);
        }

        if (cell.Attrs != CellAttrs.None)
        {
            sb.Append(" a=").Append(AttrToken(cell.Attrs));
        }

        if (cell.Width != 1)
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $" w={cell.Width}"));
        }

        if (cell.LinkUri is not null)
        {
            sb.Append(" link=").Append(cell.LinkUri);
        }

        return sb.ToString();
    }

    private static string AttrToken(CellAttrs attrs)
    {
        if (attrs == CellAttrs.None)
        {
            return "-";
        }

        var sb = new StringBuilder();
        if (attrs.HasFlag(CellAttrs.Bold))
        {
            sb.Append('B');
        }

        if (attrs.HasFlag(CellAttrs.Italic))
        {
            sb.Append('I');
        }

        if (attrs.HasFlag(CellAttrs.Underline))
        {
            sb.Append('U');
        }

        if (attrs.HasFlag(CellAttrs.Reverse))
        {
            sb.Append('R');
        }

        return sb.ToString();
    }
}
