using System;
using System.Text;

namespace Mainguard.Agents.UI.Controls;

/// <summary>
/// The P2-18 mouse-selection model (REQUIRED in TerminalGridControl v1 — the field promise of
/// 2026-07-22): a linear anchor→focus selection in the ABSOLUTE row space (scrollback ring +
/// live screen), so a selection survives damage-only redraws and live scrolling shifts it with
/// its content rather than detaching from it.
///
/// <para><see cref="ExtractText"/> implements the promised copy semantics exactly: glyph runs
/// joined with a single space where the application POSITIONED content across blank cells (the
/// Ink <c>ESC[nG</c> layout case — those cells were never written), written spaces preserved
/// verbatim, one newline between rows, trailing blanks trimmed, wide-glyph spacers skipped.</para>
/// </summary>
internal sealed class GridSelection
{
    /// <summary>An endpoint in absolute coordinates (row into ring+screen space).</summary>
    public readonly record struct Point(int Row, int Col);

    private Point _anchor;
    private Point _focus;

    public bool IsActive { get; private set; }

    /// <summary>True while the pointer is down extending the selection.</summary>
    public bool IsDragging { get; private set; }

    public void Begin(int row, int col)
    {
        _anchor = new Point(row, col);
        _focus = _anchor;
        IsActive = true;
        IsDragging = true;
    }

    public void ExtendTo(int row, int col)
    {
        if (IsDragging)
        {
            _focus = new Point(row, col);
        }
    }

    public void EndDrag() => IsDragging = false;

    public void Clear()
    {
        IsActive = false;
        IsDragging = false;
    }

    /// <summary>The selection normalized to (start ≤ end) reading order.</summary>
    public (Point Start, Point End) Normalized()
    {
        var forward = _anchor.Row < _focus.Row || (_anchor.Row == _focus.Row && _anchor.Col <= _focus.Col);
        return forward ? (_anchor, _focus) : (_focus, _anchor);
    }

    /// <summary>Whether the absolute cell is inside the (inclusive) selection — the renderer's
    /// highlight test.</summary>
    public bool Contains(int row, int col)
    {
        if (!IsActive)
        {
            return false;
        }

        var (start, end) = Normalized();
        if (row < start.Row || row > end.Row)
        {
            return false;
        }

        if (start.Row == end.Row)
        {
            return col >= start.Col && col <= end.Col;
        }

        if (row == start.Row)
        {
            return col >= start.Col;
        }

        if (row == end.Row)
        {
            return col <= end.Col;
        }

        return true;
    }

    /// <summary>Extracts the selected text from <paramref name="model"/> per the v1 copy contract.
    /// Returns an empty string when nothing is selected.</summary>
    public string ExtractText(GridModel model)
    {
        if (!IsActive)
        {
            return string.Empty;
        }

        var (start, end) = Normalized();
        var sb = new StringBuilder();

        for (var row = Math.Max(0, start.Row); row <= Math.Min(end.Row, model.TotalRows - 1); row++)
        {
            if (row > start.Row)
            {
                sb.Append('\n');
            }

            var cells = model.GetAbsoluteRow(row);
            var fromCol = row == start.Row ? Math.Max(0, start.Col) : 0;
            var toCol = row == end.Row ? Math.Min(end.Col, cells.Count - 1) : cells.Count - 1;

            var pendingGap = false;
            var wroteAny = false;
            for (var col = fromCol; col <= toCol && col < cells.Count; col++)
            {
                var cell = cells[col];
                if (cell.Width == 0)
                {
                    continue; // wide-glyph spacer
                }

                if (!cell.HasContent)
                {
                    // A positioned gap: collapses to ONE space between runs, dropped when trailing.
                    pendingGap = wroteAny;
                    continue;
                }

                if (pendingGap)
                {
                    sb.Append(' ');
                    pendingGap = false;
                }

                sb.Append(cell.Glyph.Length > 0 ? cell.Glyph : " ");
                wroteAny = true;
            }
        }

        return sb.ToString();
    }
}
