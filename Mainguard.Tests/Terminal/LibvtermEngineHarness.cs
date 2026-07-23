using System;
using Mainguard.Agents.Terminal.Vterm;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// Adapts the P2-18 libvterm engine (<see cref="VtermSession"/>) to
/// <see cref="ITerminalEngineHarness"/> — the second registration the P2-04 design reserved. It
/// drives the daemon-side session directly ("feed bytes → read grid"), so the exact engine that
/// parses agent PTYs in production is what the conformance/replay/coverage suites measure.
///
/// <para>Requires the native library (see <see cref="EngineCatalog"/> — on machines without it,
/// e.g. Windows local-dev where the libvterm engine is out of scope by design, the suites run
/// interim-only and CI enforces presence via <c>MAINGUARD_REQUIRE_LIBVTERM</c>).</para>
///
/// <para>Known gap mapped honestly: libvterm 0.3.3 does not model OSC 8 hyperlinks, so
/// <c>LinkUri</c> is always null here — recorded in <c>known-failures.libvterm.txt</c>.</para>
/// </summary>
public sealed class LibvtermEngineHarness : ITerminalEngineHarness, IDisposable
{
    private VtermSession _session = new(80, 24);

    public string EngineName => EngineCatalog.Libvterm;

    public void Reset(int cols, int rows)
    {
        _session.Dispose();
        _session = new VtermSession(cols, rows);
    }

    public void Feed(ReadOnlySpan<byte> bytes) => _session.Feed(bytes);

    public GridSnapshot ReadGrid()
    {
        var grid = _session.Snapshot();
        var cells = new GridCell[grid.Rows][];
        for (var r = 0; r < grid.Rows; r++)
        {
            cells[r] = new GridCell[grid.Cols];
            for (var c = 0; c < grid.Cols; c++)
            {
                cells[r][c] = Map(grid.Cells[r][c]);
            }
        }

        return new GridSnapshot(
            grid.Cols, grid.Rows, cells, grid.CursorRow, grid.CursorCol, grid.Modes.AltScreen);
    }

    public void Dispose() => _session.Dispose();

    private static GridCell Map(VtermCell cell)
    {
        var text = cell.Width == 0
            ? string.Empty // wide-glyph spacer (harness convention: empty text, width 0)
            : cell.HasContent && cell.Text.Length > 0 ? cell.Text : " ";

        var attrs = CellAttrs.None;
        if (cell.Attrs.HasFlag(VtermCellAttrs.Bold))
        {
            attrs |= CellAttrs.Bold;
        }

        if (cell.Attrs.HasFlag(VtermCellAttrs.Italic))
        {
            attrs |= CellAttrs.Italic;
        }

        if (cell.Attrs.HasFlag(VtermCellAttrs.Underline))
        {
            attrs |= CellAttrs.Underline;
        }

        if (cell.Attrs.HasFlag(VtermCellAttrs.Reverse))
        {
            attrs |= CellAttrs.Reverse;
        }

        return new GridCell(text, Map(cell.Fg), Map(cell.Bg), attrs, cell.Width, LinkUri: null);
    }

    private static CellColor Map(VtermColor color) => color.Kind switch
    {
        VtermColorKind.Indexed => CellColor.Indexed(color.Index),
        VtermColorKind.Rgb => CellColor.Rgb(color.R, color.G, color.B),
        _ => CellColor.Default,
    };
}
