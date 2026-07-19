using System;
using Mainguard.Agents.UI.Controls;
using Mainguard.App.Shell.Controls;
using Mainguard.UI.Controls;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// Adapts the P2-03 interim engine (<see cref="VtScreen"/>) to <see cref="ITerminalEngineHarness"/>.
///
/// <para>It drives <see cref="VtScreen"/> <b>directly</b> — the Avalonia-free parser+grid behind
/// <c>TerminalControl</c> — so the harness carries no Avalonia / vendored-renderer type coupling and
/// P2-18's future adapter can be dropped in beside it unchanged (rejection trigger:
/// "harness coupled to vendored-renderer types").</para>
///
/// <para>The interim engine tracks less than a conformant VT engine: no alternate screen, no
/// truecolor (it down-samples to the 216-colour cube), no per-cell width, no OSC 8 links, and only
/// the bold attribute. Those gaps are mapped honestly here — width is always 1, alt-screen always
/// false, links always null, attrs are bold-only — and it is precisely those gaps that the coverage
/// matrix asserts and the <c>known-failures.txt</c> allowlist records.</para>
/// </summary>
public sealed class InterimEngineHarness : ITerminalEngineHarness
{
    private VtScreen _screen = new(80, 24);

    public string EngineName => "interim";

    public void Reset(int cols, int rows) => _screen = new VtScreen(cols, rows);

    public void Feed(ReadOnlySpan<byte> bytes) => _screen.Feed(bytes);

    public GridSnapshot ReadGrid()
    {
        var grid = _screen.ReadGrid();
        var cells = new GridCell[grid.Rows][];
        for (var r = 0; r < grid.Rows; r++)
        {
            cells[r] = new GridCell[grid.Cols];
            for (var c = 0; c < grid.Cols; c++)
            {
                cells[r][c] = Map(grid.Cells[r][c]);
            }
        }

        // VtScreen has no alternate-screen model, so alt is always false here (a known gap the
        // coverage matrix's alternate-screen case asserts and the allowlist records).
        return new GridSnapshot(grid.Cols, grid.Rows, cells, grid.CursorRow, grid.CursorCol, altScreen: false);
    }

    private static GridCell Map(TerminalCell cell)
    {
        var glyph = string.IsNullOrEmpty(cell.Glyph) ? " " : cell.Glyph;
        var attrs = cell.Bold ? CellAttrs.Bold : CellAttrs.None;
        return new GridCell(
            glyph,
            ToColor(cell.Fg),
            ToColor(cell.Bg),
            attrs,
            Width: 1, // interim engine does not track double-width — always 1
            LinkUri: null); // interim engine discards OSC 8 hyperlinks
    }

    private static CellColor ToColor(int ansi) => ansi < 0 ? CellColor.Default : CellColor.Indexed(ansi);
}
