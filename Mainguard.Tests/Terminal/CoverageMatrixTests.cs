using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// P2-04 §3.4 required coverage matrix — seven feature areas, each a hand-written byte fixture (or
/// input sequence) plus the expected conformant grid/encoding, run against EVERY engine in
/// <see cref="EngineCatalog"/>. The interim engine is <b>known</b> to fail most areas; each failure
/// is recorded in its shrink-only allowlist and asserted as expected-fail (honesty rule). The P2-18
/// libvterm engine closes every output-side gap except OSC 8 (see
/// <c>known-failures.libvterm.txt</c>), and its grid client's <c>GridInputEncoder</c> closes both
/// input-side areas.
/// </summary>
public sealed class CoverageMatrixTests
{
    // ---- Output-side areas: feed bytes → read grid ----

    public static IEnumerable<object[]> OutputCaseDefinitions()
    {
        // Each case: id, cols, rows, output bytes, expected conformant grid.

        // alternate screen: enter alt, draw, leave alt → primary content restored (not overwritten).
        yield return Case(
            "coverage/alternate-screen",
            20, 3,
            "\u001b[H" + "PRIMARY" + "\u001b[?1049h" + "\u001b[H" + "ALT" + "\u001b[?1049l",
            new GridBuilder(20, 3).Put(0, 0, "PRIMARY").Cursor(0, 7).Build());

        // DEC 2026 synchronized output: markers are consumed, never leak to the screen; content clean.
        yield return Case(
            "coverage/dec2026-sync",
            20, 3,
            "\u001b[?2026h" + "\u001b[H" + "SYNC" + "\u001b[?2026l",
            new GridBuilder(20, 3).Put(0, 0, "SYNC").Cursor(0, 4).Build());

        // truecolor: 38;2;r;g;b lands in the cell fg exactly (no palette folding).
        yield return Case(
            "coverage/truecolor",
            20, 3,
            "\u001b[38;2;10;20;30m" + "X",
            new GridBuilder(20, 3).Put(0, 0, "X", fg: CellColor.Rgb(10, 20, 30)).Cursor(0, 1).Build());

        // CJK width: wide glyphs occupy two cells (lead width 2 + spacer width 0).
        yield return Case(
            "coverage/cjk-emoji-width",
            20, 3,
            "你好",
            new GridBuilder(20, 3).PutWide(0, 0, "你").PutWide(0, 2, "好").Cursor(0, 4).Build());

        // OSC 8 hyperlinks: link uri attached to the covered cells; both ST and BEL terminators.
        yield return Case(
            "coverage/osc8-hyperlink",
            20, 3,
            "\u001b]8;;https://a.example\u001b\\A\u001b]8;;\u001b\\"   // ST-terminated
            + "\r\n" + "\u001b]8;;https://b.example\aB\u001b]8;;\a", // BEL-terminated
            new GridBuilder(20, 3)
                .Put(0, 0, "A", link: "https://a.example")
                .Put(1, 0, "B", link: "https://b.example")
                .Cursor(1, 1)
                .Build());
    }

    public static IEnumerable<object[]> OutputCases()
    {
        foreach (var engine in EngineCatalog.AvailableEngines)
        {
            foreach (var row in OutputCaseDefinitions())
            {
                yield return new object[] { engine, row[0], row[1], row[2], row[3], row[4] };
            }
        }
    }

    public static IEnumerable<object[]> Engines()
    {
        foreach (var engine in EngineCatalog.AvailableEngines)
        {
            yield return new object[] { engine };
        }
    }

    [Theory]
    [MemberData(nameof(OutputCases))]
    public void CoverageMatrix_OutputArea(string engineName, string caseId, int cols, int rows, byte[] bytes, GridSnapshot expected)
    {
        var engine = EngineCatalog.Create(engineName);
        engine.Reset(cols, rows);
        engine.Feed(bytes);
        var actual = engine.ReadGrid();

        AssertConformance(engineName, caseId, expected.GridEquals(actual), () => expected.DiffReport(actual));
    }

    // ---- Input-side areas: engine input encoder → bytes ----

    [Theory]
    [MemberData(nameof(Engines))]
    public void CoverageMatrix_BracketedPaste(string engineName)
    {
        // A conformant engine, once ?2004h is active, wraps pasted text in ESC[200~ … ESC[201~.
        var expected = Encode("\u001b[200~hello\u001b[201~");
        var actual = EngineCatalog.EncodePaste(engineName, "hello", bracketedPasteActive: true);

        AssertConformance(
            engineName,
            "coverage/bracketed-paste",
            actual is not null && SequenceEqual(expected, actual),
            () => "the engine's input path emits no bracketed-paste framing (sends raw text)");
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void CoverageMatrix_MouseReporting(string engineName)
    {
        // With SGR mouse mode (?1006h) a left click at col 5, row 3 (1-based) encodes ESC[<0;5;3M.
        var expected = Encode("\u001b[<0;5;3M");
        var actual = EngineCatalog.EncodeMouseClick(engineName, button: 0, col: 5, row: 3, sgr: true);

        AssertConformance(
            engineName,
            "coverage/mouse-reporting",
            actual is not null && SequenceEqual(expected, actual),
            () => "the engine's input path has no mouse encoder");
    }

    /// <summary>
    /// Allowlist-aware assertion shared by every area. A listed (expected-fail) case must NOT match;
    /// if it starts matching, the suite fails demanding its removal from that engine's allowlist. A
    /// non-listed case must match, printing a readable diff on failure.
    /// </summary>
    private static void AssertConformance(string engineName, string caseId, bool matches, System.Func<string> diff)
    {
        if (EngineCatalog.AllowlistFor(engineName).Contains(caseId))
        {
            Assert.False(
                matches,
                $"Case '{caseId}' is allowlisted as expected-fail for '{engineName}' but now PASSES. "
                + $"Remove it from {EngineCatalog.AllowlistFileFor(engineName)} — the allowlist only shrinks.");
        }
        else
        {
            Assert.True(matches, $"Coverage case '{caseId}' failed on '{engineName}':\n{diff()}");
        }
    }

    private static object[] Case(string id, int cols, int rows, string bytes, GridSnapshot expected)
        => new object[] { id, cols, rows, Encode(bytes), expected };

    private static byte[] Encode(string s) => Encoding.UTF8.GetBytes(s);

    private static bool SequenceEqual(byte[] a, byte[] b)
        => a.Length == b.Length && a.AsSpan().SequenceEqual(b);
}
