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
/// <see cref="ITerminalEngineHarness"/> against EVERY engine in <see cref="EngineCatalog"/> (the
/// P2-04 invariant: both engines run the same suites; P2-18's libvterm harness slots in here with
/// no other change). Cases an engine handles pass outright; cases it does not are recorded in its
/// shrink-only allowlist file and asserted as expected-fail.
///
/// <para>Follow-up: record a representative subset of real vttest pages in the Linux container
/// (<c>docker compose run --rm shell</c>, apt-install vttest, drive it in a PTY via
/// <see cref="TranscriptRecorder"/>) and add them here as additional cases; the allowlist and runner
/// are already in place.</para>
/// </summary>
public sealed class VtConformanceTests
{
    private const string E = "\u001b";

    public static IEnumerable<object[]> CaseDefinitions()
    {
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

        // DECSTBM scroll region: scrolling within rows 2–4 must leave row 0 ("TOP") fixed, and the
        // final "X" prints at the region-bottom cursor. The interim engine ignores CSI r and
        // scrolls the whole screen, losing "TOP" from row 0. (Expected grid corrected in P2-18 —
        // the original omitted the X write; validated against the conformant libvterm engine.)
        yield return Case(
            "vttest/scroll-region-decstbm",
            20, 5,
            E + "[H" + "TOP" + E + "[2;4r" + E + "[4;1H" + "\n\n\n" + "X",
            new GridBuilder(20, 5).Put(0, 0, "TOP").Put(3, 0, "X").Cursor(3, 1).Build());

        // DECSC/DECRC (ESC 7 / ESC 8): save at (2,5), move+write, restore, write again at (2,5).
        // The interim engine ignores both, so the second write lands at the moved position instead.
        yield return Case(
            "vttest/save-restore-cursor",
            20, 4,
            E + "[2;5H" + E + "7" + E + "[4;1H" + "X" + E + "8" + "Y",
            new GridBuilder(20, 4).Put(3, 0, "X").Put(1, 4, "Y").Cursor(1, 5).Build());

    }

    /// <summary>
    /// The P2-18 field-findings repertoire (master doc P2-18 block, 2026-07-22): the sequences Ink
    /// full-screen redraws depend on and the interim engine is KNOWN not to implement. These run
    /// against the libvterm engine only and must pass outright — they cannot join
    /// <see cref="CaseDefinitions"/> because the interim allowlist is shrink-only (CI-enforced), and
    /// proving the interim engine fails them is P2-03 §9's already-accepted point, not new signal.
    /// </summary>
    public static IEnumerable<object[]> InkRepertoireDefinitions()
    {
        // Insert/delete line (CSI L / CSI M): the field failure mode was stale rows left behind.
        yield return Case(
            "vttest/insert-delete-line",
            20, 4,
            "AAA\r\nBBB\r\nCCC" + E + "[2;1H" + E + "[L" + "NEW" + E + "[1;1H" + E + "[M",
            new GridBuilder(20, 4).Put(0, 0, "NEW").Put(1, 0, "BBB").Put(2, 0, "CCC").Cursor(0, 0).Build());

        // Insert/delete character (CSI @ / CSI P) within a row.
        yield return Case(
            "vttest/insert-delete-char",
            20, 3,
            "ABCD" + E + "[1;2H" + E + "[2@" + "xy" + E + "[1;1H" + E + "[P",
            new GridBuilder(20, 3).Put(0, 0, "xyBCD").Cursor(0, 0).Build());

        // Deferred wrap (xterm last-column semantics): writing the last column does NOT wrap until
        // the next glyph arrives; an immediate CR keeps the cursor on the same row.
        yield return Case(
            "vttest/deferred-wrap",
            5, 3,
            "ABCDE\rZ",
            new GridBuilder(5, 3).Put(0, 0, "ZBCDE").Cursor(0, 1).Build());

        // ED 2 (erase all, cursor stays) and ED 3 (xterm: also clears scrollback; on-screen effect
        // equals ED 2) — the erase variants the interim engine folded together incorrectly.
        yield return Case(
            "vttest/erase-display-2-3",
            20, 3,
            "AAA\r\nBBB" + E + "[2;2H" + E + "[2J" + E + "[3J" + "X",
            new GridBuilder(20, 3).Put(1, 1, "X").Cursor(1, 2).Build());

        // Origin mode (DECOM): with a scroll region set and origin mode on, CUP is region-relative.
        yield return Case(
            "vttest/origin-mode",
            20, 5,
            E + "[2;4r" + E + "[?6h" + E + "[1;1H" + "O",
            new GridBuilder(20, 5).Put(1, 0, "O").Cursor(1, 1).Build());
    }

    public static IEnumerable<object[]> InkRepertoireCases()
    {
        if (!System.Linq.Enumerable.Contains(EngineCatalog.AvailableEngines, EngineCatalog.Libvterm))
        {
            // xUnit v2 rejects an empty member-data set; where libvterm is absent (Windows
            // local-dev) a sentinel row keeps the theory discoverable and the body no-ops.
            // EngineCatalogTests turns absence into a hard failure where CI requires the engine.
            yield return new object[] { LibvtermUnavailableSentinel, 0, 0, System.Array.Empty<byte>(), GridSnapshot.Blank(1, 1) };
            yield break;
        }

        foreach (var row in InkRepertoireDefinitions())
        {
            yield return row;
        }
    }

    private const string LibvtermUnavailableSentinel = "<libvterm-unavailable>";

    [Theory]
    [MemberData(nameof(InkRepertoireCases))]
    public void VtConformance_InkRepertoire_Libvterm(string caseId, int cols, int rows, byte[] bytes, GridSnapshot expected)
    {
        if (caseId == LibvtermUnavailableSentinel)
        {
            Assert.False(EngineCatalog.LibvtermRequired, "libvterm is required here but unavailable.");
            return;
        }

        var engine = EngineCatalog.Create(EngineCatalog.Libvterm);
        engine.Reset(cols, rows);
        engine.Feed(bytes);
        var actual = engine.ReadGrid();
        Assert.True(
            expected.GridEquals(actual),
            $"Ink-repertoire case '{caseId}' failed on libvterm:\n{expected.DiffReport(actual)}");
    }

    /// <summary>Every conformance case × every runnable engine.</summary>
    public static IEnumerable<object[]> Cases()
    {
        foreach (var engine in EngineCatalog.AvailableEngines)
        {
            foreach (var row in CaseDefinitions())
            {
                yield return new object[] { engine, row[0], row[1], row[2], row[3], row[4] };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void VtConformance_Vttest(string engineName, string caseId, int cols, int rows, byte[] bytes, GridSnapshot expected)
    {
        var engine = EngineCatalog.Create(engineName);
        engine.Reset(cols, rows);
        engine.Feed(bytes);
        var actual = engine.ReadGrid();
        var matches = expected.GridEquals(actual);

        if (EngineCatalog.AllowlistFor(engineName).Contains(caseId))
        {
            Assert.False(
                matches,
                $"Conformance case '{caseId}' is allowlisted as expected-fail for '{engineName}' but now PASSES. "
                + $"Remove it from {EngineCatalog.AllowlistFileFor(engineName)} — the allowlist only shrinks.");
        }
        else
        {
            Assert.True(matches, $"Conformance case '{caseId}' failed on '{engineName}':\n{expected.DiffReport(actual)}");
        }
    }

    /// <summary>
    /// Test-contract #2 (`VtConformance_AllowlistedStillFail`): every id in an engine's allowlist
    /// must correspond to a real, still-failing case — no stale entries. Iterates the allowlisted
    /// conformance cases per engine and asserts each still diverges.
    /// </summary>
    [Fact]
    public void VtConformance_AllowlistedStillFail()
    {
        foreach (var engineName in EngineCatalog.AvailableEngines)
        {
            var allowlist = EngineCatalog.AllowlistFor(engineName);
            foreach (var row in CaseDefinitions())
            {
                var caseId = (string)row[0];
                if (!allowlist.Contains(caseId))
                {
                    continue;
                }

                var cols = (int)row[1];
                var rows = (int)row[2];
                var bytes = (byte[])row[3];
                var expected = (GridSnapshot)row[4];

                var engine = EngineCatalog.Create(engineName);
                engine.Reset(cols, rows);
                engine.Feed(bytes);
                Assert.False(
                    expected.GridEquals(engine.ReadGrid()),
                    $"Allowlisted case '{caseId}' unexpectedly matches the conformant grid on '{engineName}' — "
                    + $"remove it from {EngineCatalog.AllowlistFileFor(engineName)}.");
            }
        }
    }

    /// <summary>The libvterm allowlist must be a subset of the interim one — that IS the ≥-parity
    /// merge gate in file form (P2-18 may only close gaps, never open one the interim engine
    /// doesn't have).</summary>
    [Fact]
    public void LibvtermAllowlist_IsSubsetOfInterim()
    {
        var interim = EngineCatalog.AllowlistFor(EngineCatalog.Interim);
        foreach (var id in EngineCatalog.AllowlistFor(EngineCatalog.Libvterm))
        {
            Assert.True(
                interim.Contains(id),
                $"'{id}' is allowlisted for libvterm but not for interim — the libvterm engine would regress it.");
        }
    }

    private static object[] Case(string id, int cols, int rows, string bytes, GridSnapshot expected)
        => new object[] { id, cols, rows, Encoding.UTF8.GetBytes(bytes), expected };
}
