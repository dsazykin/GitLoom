using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mainguard.Agents.Terminal.Vterm;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// Pure tests for <see cref="TerminalModeTracker"/> — the daemon-side scanner for the modes
/// libvterm keeps to itself (bracketed paste, DECCKM, SGR mouse) and the OSC 52 clipboard bridge.
/// This is where the P2-18 never-answer-queries rule now lives daemon-side; the same rule is
/// pinned client-side for the interim engine by <c>TerminalClipboardTests</c>.
/// </summary>
public sealed class TerminalModeTrackerTests
{
    private const string Esc = "\u001b";

    private static (TerminalModeTracker Tracker, List<string> Copies) NewTracker()
    {
        var tracker = new TerminalModeTracker();
        var copies = new List<string>();
        tracker.ClipboardCopyRequested += copies.Add;
        return (tracker, copies);
    }

    private static void Feed(TerminalModeTracker tracker, string text) =>
        tracker.Feed(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void DecsetModes_TrackSetAndReset()
    {
        var (tracker, _) = NewTracker();
        Feed(tracker, $"{Esc}[?2004h{Esc}[?1h{Esc}[?1006h");
        Assert.True(tracker.BracketedPaste);
        Assert.True(tracker.CursorKeysApplication);
        Assert.True(tracker.MouseSgr);

        Feed(tracker, $"{Esc}[?2004l{Esc}[?1l{Esc}[?1006l");
        Assert.False(tracker.BracketedPaste);
        Assert.False(tracker.CursorKeysApplication);
        Assert.False(tracker.MouseSgr);
    }

    [Fact]
    public void CombinedDecset_AndFullReset()
    {
        var (tracker, _) = NewTracker();
        Feed(tracker, $"{Esc}[?1000;1006;2004h");
        Assert.True(tracker.MouseSgr);
        Assert.True(tracker.BracketedPaste);

        Feed(tracker, $"{Esc}c"); // RIS clears private modes
        Assert.False(tracker.MouseSgr);
        Assert.False(tracker.BracketedPaste);
    }

    [Fact]
    public void Osc52_Set_DecodesWithBothTerminators_AndAcrossSplits()
    {
        var (tracker, copies) = NewTracker();
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("copy-me"));

        Feed(tracker, $"{Esc}]52;c;{payload}");
        Feed(tracker, $"{Esc}]52;c;{payload}{Esc}\\");

        var split = $"{Esc}]52;c;{payload}";
        Feed(tracker, split[..7]);
        Feed(tracker, split[7..]);

        Assert.Equal(new[] { "copy-me", "copy-me", "copy-me" }, copies);
    }

    [Fact]
    public void Osc52_Query_IsNeverAnsweredOrRaised()
    {
        var (tracker, copies) = NewTracker();
        Feed(tracker, $"{Esc}]52;c;?");
        Assert.Empty(copies);
    }

    [Fact]
    public void Osc52_InvalidBase64_AndOtherOscs_AreIgnored()
    {
        var (tracker, copies) = NewTracker();
        Feed(tracker, $"{Esc}]52;c;!!!{Esc}]0;title{Esc}]8;;https://x{Esc}\\");
        Assert.Empty(copies);
    }

    [Fact]
    public void EndlessOsc_IsBounded_AndDiscardedWhole()
    {
        var (tracker, copies) = NewTracker();
        Feed(tracker, $"{Esc}]52;c;");
        var junk = new string('A', 150_000);
        Feed(tracker, junk);
        Feed(tracker, "");
        Assert.Empty(copies); // overflowed capture is discarded, never partially decoded
    }
}

/// <summary>
/// The daemon-side libvterm engine (<see cref="VtermSession"/>) beyond what the P2-04 harness
/// covers: the drained delta shape (scroll ops + bounded damage — the traffic invariant), the
/// scrollback ring (cap, absolute indexing, pop-line round-trip), resize → snapshot semantics, the
/// mode surface, and the single-thread guard. Skipped where the native library is absent (CI
/// requires it via MAINGUARD_REQUIRE_LIBVTERM + EngineCatalogTests).
/// </summary>
public sealed class VtermSessionTests
{
    private const string Esc = "\u001b";

    private static void Feed(VtermSession session, string text) => session.Feed(Encoding.UTF8.GetBytes(text));

    [RequiresLibvtermFact]
    public void SteadyScroll_ProducesScrollOpsAndBoundedDamage_NeverFullGrid()
    {
        using var session = new VtermSession(20, 5);
        session.DrainDelta(); // consume the initial snapshot state

        Feed(session, string.Join("\r\n", Enumerable.Range(0, 5).Select(i => $"line{i}")));
        session.DrainDelta();

        // One more line: the screen scrolls by one — the delta must be a scroll op + O(1) damage.
        Feed(session, "\r\nline5");
        var delta = session.DrainDelta();

        Assert.False(session.SnapshotPending);
        Assert.Contains(delta.Ops, op => op is VtermGridOp.Scroll { RowDelta: < 0 });
        Assert.Single(delta.PushedRows);
        Assert.True(delta.DamagedRows.Count <= 2, $"steady scroll damaged {delta.DamagedRows.Count} rows");
        Assert.Equal("line0", RowText(delta.PushedRows[0]));
    }

    [RequiresLibvtermFact]
    public void ScrollbackRing_Caps_AndServesAbsoluteIndexes()
    {
        using var session = new VtermSession(10, 3);
        for (var i = 0; i < 10; i++)
        {
            Feed(session, $"r{i}\r\n");
        }

        Assert.True(session.ScrollbackCount >= 7);
        var rows = session.GetScrollback(0, 2);
        Assert.Equal(0, rows[0].Index);
        Assert.Equal("r0", RowText(rows[0].Cells));
        Assert.Equal("r1", RowText(rows[1].Cells));
    }

    [RequiresLibvtermFact]
    public void Resize_SetsSnapshotPending_AndReflows()
    {
        using var session = new VtermSession(20, 5);
        session.DrainDelta();
        Feed(session, "hello");

        session.Resize(30, 8);
        Assert.True(session.SnapshotPending);
        session.DrainDelta();

        var grid = session.Snapshot();
        Assert.Equal(30, grid.Cols);
        Assert.Equal(8, grid.Rows);
        Assert.Equal("hello", grid.RowText(0));
    }

    [RequiresLibvtermFact]
    public void Modes_CombineVtermProps_AndTrackerState()
    {
        using var session = new VtermSession(20, 5);
        Feed(session, $"{Esc}[?1049h{Esc}[?1000h{Esc}[?1006h{Esc}[?2004h{Esc}[?1h");

        var modes = session.Modes;
        Assert.True(modes.AltScreen);
        Assert.Equal(1, modes.MouseMode);
        Assert.True(modes.MouseSgr);
        Assert.True(modes.BracketedPaste);
        Assert.True(modes.CursorKeysApplication);
    }

    [RequiresLibvtermFact]
    public void Osc52_SurfacesTheClipboardEvent_QueriesNever()
    {
        using var session = new VtermSession(20, 5);
        var copies = new List<string>();
        session.ClipboardCopyRequested += copies.Add;

        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("jail-copy"));
        Feed(session, $"{Esc}]52;c;{payload}{Esc}]52;c;?");

        Assert.Equal(new[] { "jail-copy" }, copies);
    }

    [RequiresLibvtermFact]
    public void WideGlyphs_SurviveTheScrollbackPushPopRoundTrip()
    {
        // Shrink then regrow the screen: rows pushed into the ring on shrink pop back on grow —
        // sb_popline reconstructs the native cells from our compact rows, wide glyphs included.
        using var session = new VtermSession(10, 4);
        Feed(session, "你好ab\r\none\r\ntwo\r\nthree");

        session.Resize(10, 2);
        session.DrainDelta();
        session.Resize(10, 4);
        session.DrainDelta();

        var grid = session.Snapshot();
        var texts = Enumerable.Range(0, 4).Select(grid.RowText).ToArray();
        Assert.Contains("你好ab", texts);
        Assert.Equal(2, grid.Cells[Array.IndexOf(texts, "你好ab")][0].Width);
    }

    private static string RowText(IReadOnlyList<VtermCell> cells)
    {
        var sb = new StringBuilder();
        foreach (var cell in cells)
        {
            if (cell.Width == 0)
            {
                continue;
            }

            sb.Append(cell.HasContent && cell.Text.Length > 0 ? cell.Text : " ");
        }

        return sb.ToString().TrimEnd();
    }
}
