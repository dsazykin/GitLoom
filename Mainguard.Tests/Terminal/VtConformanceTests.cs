using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// P2-04 §3.2 conformance layer. Real <c>vttest</c>/<c>esctest</c> are menu-driven interactive
/// binaries; capturing their per-page output requires driving them in a PTY once and committing the
/// bytes. That recording is a documented follow-up (see the class README note below). In its place —
/// and covering the same feature areas — this suite ships hand-written vttest-style fixtures: a byte
/// script per feature page plus the expected conformant grid, run through the shared
/// <see cref="ITerminalEngineHarness"/> with the same shrink-only allowlist mechanism as the coverage
/// matrix. Cases the interim engine handles pass outright; cases it does not are recorded in
/// <c>known-failures.txt</c> and asserted as expected-fail.
///
/// <para>Follow-up: record a representative subset of real vttest pages in the Linux container
/// (<c>docker compose run --rm shell</c>, apt-install vttest, drive it in a PTY via
/// <see cref="TranscriptRecorder"/>) and add them here as additional cases; the allowlist and runner
/// are already in place.</para>
/// </summary>
public sealed class VtConformanceTests
{
    private const string E = "\u001b";

    private static readonly IReadOnlySet<string> Allowlist = TerminalHarnessPaths.LoadAllowlist();

    public static IEnumerable<object[]> Cases()
    {
        // --- pages the interim engine handles (not allowlisted) ---

        // Cursor addressing (CUP) + relative motion (CUF).
        yield return Case(
            "vttest/cursor-movement",
            20, 5,
            E + "[2;3HA" + E + "[2CB",
            new GridBuilder(20, 5).Put(1, 2, "A").Put(1, 5, "B").Cursor(1, 6).Build());

        // Erase in Display (ED, mode 0 from home clears all).
        yield return Case(
            "vttest/erase-display",
            20, 4,
            E + "[H" + "AAAA" + "\r\n" + "BBBB" + E + "[H" + E + "[J",
            new GridBuilder(20, 4).Cursor(0, 0).Build());

        // Erase in Line (EL, mode 0 clears cursor→eol).
        yield return Case(
            "vttest/erase-line",
            20, 3,
            "ABCDEFGH" + E + "[5G" + E + "[K",
            new GridBuilder(20, 3).Put(0, 0, "ABCD").Cursor(0, 4).Build());

        // SGR: bold + red, then reset.
        yield return Case(
            "vttest/sgr-basic",
            20, 3,
            E + "[1;31mR" + E + "[0mN",
            new GridBuilder(20, 3)
                .Put(0, 0, "R", fg: CellColor.Indexed(1), attrs: CellAttrs.Bold)
                .Put(0, 1, "N")
                .Cursor(0, 2)
                .Build());

        // Horizontal tab lands on the next 8-column stop.
        yield return Case(
            "vttest/tab-stops",
            20, 2,
            "A\tB",
            new GridBuilder(20, 2).Put(0, 0, "A").Put(0, 8, "B").Cursor(0, 9).Build());

        // --- pages the interim engine does NOT handle (allowlisted, expected-fail) ---

        // DECSTBM scroll region: scrolling within rows 2–4 must leave row 0 ("TOP") fixed. The
        // interim engine ignores CSI r and scrolls the whole screen, losing "TOP" from row 0.
        yield return Case(
            "vttest/scroll-region-decstbm",
            20, 5,
            E + "[H" + "TOP" + E + "[2;4r" + E + "[4;1H" + "\n\n\n" + "X",
            new GridBuilder(20, 5).Put(0, 0, "TOP").Cursor(3, 0).Build());

        // DECSC/DECRC (ESC 7 / ESC 8): save at (2,5), move+write, restore, write again at (2,5).
        // The interim engine ignores both, so the second write lands at the moved position instead.
        yield return Case(
            "vttest/save-restore-cursor",
            20, 4,
            E + "[2;5H" + E + "7" + E + "[4;1H" + "X" + E + "8" + "Y",
            new GridBuilder(20, 4).Put(3, 0, "X").Put(1, 4, "Y").Cursor(1, 5).Build());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void VtConformance_Vttest(string caseId, int cols, int rows, byte[] bytes, GridSnapshot expected)
    {
        var engine = new InterimEngineHarness();
        engine.Reset(cols, rows);
        engine.Feed(bytes);
        var actual = engine.ReadGrid();
        var matches = expected.GridEquals(actual);

        if (Allowlist.Contains(caseId))
        {
            Assert.False(
                matches,
                $"Conformance case '{caseId}' is allowlisted as expected-fail but now PASSES. "
                + "Remove it from Mainguard.Tests/Terminal/known-failures.txt — the allowlist only shrinks.");
        }
        else
        {
            Assert.True(matches, $"Conformance case '{caseId}' failed:\n{expected.DiffReport(actual)}");
        }
    }

    /// <summary>
    /// Test-contract #2 (`VtConformance_AllowlistedStillFail`): every id in the allowlist must
    /// correspond to a real, still-failing case — no stale entries. Iterates the allowlisted
    /// conformance/coverage cases run through the interim engine and asserts each still diverges.
    /// </summary>
    [Fact]
    public void VtConformance_AllowlistedStillFail()
    {
        foreach (var row in Cases())
        {
            var caseId = (string)row[0];
            if (!Allowlist.Contains(caseId))
            {
                continue;
            }

            var cols = (int)row[1];
            var rows = (int)row[2];
            var bytes = (byte[])row[3];
            var expected = (GridSnapshot)row[4];

            var engine = new InterimEngineHarness();
            engine.Reset(cols, rows);
            engine.Feed(bytes);
            Assert.False(
                expected.GridEquals(engine.ReadGrid()),
                $"Allowlisted case '{caseId}' unexpectedly matches the conformant grid — remove it from the allowlist.");
        }
    }

    private static object[] Case(string id, int cols, int rows, string bytes, GridSnapshot expected)
        => new object[] { id, cols, rows, Encoding.UTF8.GetBytes(bytes), expected };
}
