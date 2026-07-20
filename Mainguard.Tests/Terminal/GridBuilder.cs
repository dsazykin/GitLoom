using System.Globalization;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// Fluent builder for the <b>expected</b> (conformant) grid in a coverage-matrix case. Starts blank
/// and lets a test place styled text, wide glyphs, links, cursor, and the alt-screen flag — the
/// authoritative "what a correct VT engine produces" that the interim engine is measured against.
/// </summary>
public sealed class GridBuilder
{
    private readonly GridSnapshot _blank;
    private readonly GridCell[][] _cells;
    private int _cursorRow;
    private int _cursorCol;
    private bool _alt;

    public GridBuilder(int cols, int rows)
    {
        Cols = cols;
        Rows = rows;
        _blank = GridSnapshot.Blank(cols, rows);
        _cells = _blank.Cells;
    }

    public int Cols { get; }
    public int Rows { get; }

    /// <summary>Places narrow text starting at (row,col) with optional styling.</summary>
    public GridBuilder Put(
        int row,
        int col,
        string text,
        CellColor? fg = null,
        CellColor? bg = null,
        CellAttrs attrs = CellAttrs.None,
        string? link = null)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var c = col;
        while (enumerator.MoveNext())
        {
            _cells[row][c] = new GridCell(
                (string)enumerator.Current,
                fg ?? CellColor.Default,
                bg ?? CellColor.Default,
                attrs,
                1,
                link);
            c++;
        }

        return this;
    }

    /// <summary>Places a double-width grapheme: lead cell width 2, trailing spacer width 0.</summary>
    public GridBuilder PutWide(int row, int col, string grapheme, CellColor? fg = null)
    {
        _cells[row][col] = new GridCell(grapheme, fg ?? CellColor.Default, CellColor.Default, CellAttrs.None, 2, null);
        _cells[row][col + 1] = new GridCell(string.Empty, CellColor.Default, CellColor.Default, CellAttrs.None, 0, null);
        return this;
    }

    public GridBuilder Cursor(int row, int col)
    {
        _cursorRow = row;
        _cursorCol = col;
        return this;
    }

    public GridBuilder Alt(bool alt = true)
    {
        _alt = alt;
        return this;
    }

    public GridSnapshot Build() =>
        new(Cols, Rows, _cells, _cursorRow, _cursorCol, _alt);
}
