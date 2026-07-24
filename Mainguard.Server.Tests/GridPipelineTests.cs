using System.Text;
using Mainguard.Agents.Terminal.Vterm;
using Mainguard.Agents.UI.Controls;
using Mainguard.Protos.V1;
using Mainguard.Server.Terminal;
using Mainguard.Server.Tests.Fixtures;
using Xunit.Abstractions;

namespace Mainguard.Server.Tests;

/// <summary>
/// The deterministic P2-18 pipeline proof, no timing anywhere: daemon engine
/// (<see cref="VtermSession"/>) → wire (<see cref="GridUpdateBuilder"/>) → client mirror
/// (<see cref="GridModel"/>).
///
/// <list type="bullet">
/// <item><b>Snapshot/attach identity:</b> replaying the recorded htop transcript and attaching via
/// snapshot renders a client grid identical to the server's, cell by cell — the "kill client
/// mid-htop → reattach identical" invariant in deterministic form.</item>
/// <item><b>Streaming mirror:</b> applying the per-chunk delta stream reproduces the same grid as
/// the snapshot — no op-ordering bug survives this.</item>
/// <item><b>Traffic invariant + perf measurement:</b> a 1000-line scroll produces scroll ops and
/// bounded damage, never a full-grid resend; the measured bytes land in the test output for the PR.
/// </item>
/// </list>
/// </summary>
public sealed class GridPipelineTests
{
    private readonly ITestOutputHelper _output;

    public GridPipelineTests(ITestOutputHelper output) => _output = output;

    [RequiresLibvtermFact]
    public void SnapshotAttach_HtopTranscript_ClientGridIdentical()
    {
        using var session = new VtermSession(80, 24);
        session.Feed(Transcript("htop-60s"));
        session.DrainDelta();

        var client = new GridModel();
        client.ApplyGrid(GridUpdateBuilder.BuildSnapshot(session.Snapshot()));

        AssertMirrors(session, client);
    }

    [RequiresLibvtermFact]
    public void StreamingDeltas_ChunkedTranscript_MirrorTheServerGrid()
    {
        using var session = new VtermSession(80, 24);
        var client = new GridModel();
        client.ApplyGrid(GridUpdateBuilder.BuildSnapshot(session.Snapshot())); // attach on the blank grid
        session.DrainDelta();

        var bytes = Transcript("htop-60s");
        var offset = 0;
        var rng = new Random(42);
        while (offset < bytes.Length)
        {
            var take = Math.Min(rng.Next(256, 4096), bytes.Length - offset);
            session.Feed(bytes.AsSpan(offset, take));
            offset += take;

            var delta = GridUpdateBuilder.BuildDelta(session.DrainDelta());
            if (delta is not null)
            {
                client.ApplyGrid(delta);
            }
        }

        AssertMirrors(session, client);
    }

    [RequiresLibvtermFact]
    public void SteadyScroll_1000Lines_ScrollOpsNotFullGrids_WithMeasuredBudget()
    {
        const int lines = 1000;
        using var session = new VtermSession(80, 24);
        var client = new GridModel();
        client.ApplyGrid(GridUpdateBuilder.BuildSnapshot(session.Snapshot()));
        session.DrainDelta();

        long totalBytes = 0;
        long contentBytes = 0;
        var updates = 0;
        var maxDamageRows = 0;
        var scrollOps = 0;

        for (var i = 0; i < lines; i += 10)
        {
            var chunk = new StringBuilder();
            for (var j = i; j < i + 10; j++)
            {
                chunk.Append($"scroll-line-{j:0000} content abcdefghijklmnop\r\n");
            }

            var bytes = Encoding.UTF8.GetBytes(chunk.ToString());
            contentBytes += bytes.Length;
            session.Feed(bytes);

            var drained = session.DrainDelta();
            Assert.False(session.SnapshotPending, "steady scroll must never force a snapshot");
            var delta = GridUpdateBuilder.BuildDelta(drained);
            if (delta is null)
            {
                continue;
            }

            Assert.False(delta.Snapshot, "steady scroll produced a full-grid send");
            updates++;
            maxDamageRows = Math.Max(maxDamageRows, delta.Damage.Count);
            foreach (var op in delta.Ops)
            {
                if (op.OpCase == GridOp.OpOneofCase.Scroll)
                {
                    scrollOps++;
                }
            }

            totalBytes += delta.CalculateSize();
            client.ApplyGrid(delta);
        }

        AssertMirrors(session, client);
        Assert.True(scrollOps > 0, "no scroll ops observed in a steady scroll");
        Assert.True(
            maxDamageRows <= 24,
            $"a scroll tick damaged {maxDamageRows} rows — more than the visible screen");

        // Budget: the grid traffic must stay within a small factor of the raw content itself
        // (a full-grid resend per scrolled line would be ~24× the content).
        Assert.True(
            totalBytes < contentBytes * 4,
            $"grid traffic {totalBytes} B exceeded 4× the {contentBytes} B content");

        _output.WriteLine(
            $"P2-18 damage-coalescing measurement: {lines} scrolled lines, content {contentBytes} B → " +
            $"{updates} GridUpdates, {totalBytes} B on the wire ({totalBytes / (double)lines:F1} B/line), " +
            $"{scrollOps} scroll ops, max damage rows/update {maxDamageRows}.");
    }

    [RequiresLibvtermFact]
    public void Resize_RingStaysConsistent_AndSnapshotCarriesNewGeometry()
    {
        using var session = new VtermSession(20, 6);
        var client = new GridModel();
        client.ApplyGrid(GridUpdateBuilder.BuildSnapshot(session.Snapshot()));
        session.DrainDelta();

        for (var i = 0; i < 12; i++)
        {
            session.Feed(Encoding.UTF8.GetBytes($"row-{i}\r\n"));
        }

        var pre = GridUpdateBuilder.BuildDelta(session.DrainDelta());
        Assert.NotNull(pre);
        client.ApplyGrid(pre!);
        var ringBefore = client.ScrollbackCount;
        Assert.Equal(session.ScrollbackCount, ringBefore);

        // Shrink: rows push into the ring; the client must apply the ring-only update then the
        // snapshot (mirrors BoundTerminalSession.PublishSnapshotLocked's split).
        session.Resize(20, 3);
        Assert.True(session.SnapshotPending);
        var drained = session.DrainDelta();
        foreach (var pushed in drained.PushedRows)
        {
            var ringOnly = new GridUpdate { Cols = 20, Rows = 3 };
            ringOnly.Pushed.Add(GridUpdateBuilder.BuildRow(0, pushed));
            client.ApplyGrid(ringOnly);
        }

        foreach (var op in drained.Ops)
        {
            if (op is VtermGridOp.PopRows pop)
            {
                var ringOnly = new GridUpdate { Cols = 20, Rows = 3 };
                ringOnly.Ops.Add(new GridOp { PopRows = (uint)pop.Count });
                client.ApplyGrid(ringOnly);
            }
        }

        client.ApplyGrid(GridUpdateBuilder.BuildSnapshot(session.Snapshot()));

        Assert.Equal(3, client.Rows);
        Assert.Equal(session.ScrollbackCount, client.ScrollbackCount);
        AssertMirrors(session, client);
    }

    // ---- helpers ----

    private static byte[] Transcript(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "Mainguard.Tests", "Transcripts", name + ".bytes");
        Assert.True(File.Exists(path), $"missing transcript {path}");
        return File.ReadAllBytes(path);
    }

    /// <summary>Cell-by-cell identity between the server grid and the client mirror.</summary>
    internal static void AssertMirrors(VtermSession session, GridModel client)
    {
        var server = session.Snapshot();
        Assert.Equal(server.Cols, client.Cols);
        Assert.Equal(server.Rows, client.Rows);
        Assert.Equal(server.CursorRow, client.CursorRow);
        Assert.Equal(server.CursorCol, client.CursorCol);
        Assert.Equal(server.Modes.AltScreen, client.AltScreen);
        Assert.Equal(server.Modes.BracketedPaste, client.BracketedPaste);

        for (var r = 0; r < server.Rows; r++)
        {
            var clientRow = client.GetScreenRow(r);
            for (var c = 0; c < server.Cols; c++)
            {
                var s = server.Cells[r][c];
                var k = clientRow[c];
                var where = $"cell ({r},{c})";
                Assert.True(s.HasContent == k.HasContent, $"{where}: content flag diverged");
                if (s.HasContent)
                {
                    Assert.True(s.Text == k.Glyph, $"{where}: '{s.Text}' vs '{k.Glyph}'");
                }

                Assert.True((byte)s.Attrs == k.Attrs, $"{where}: attrs diverged");
                Assert.True(GridUpdateBuilder.EncodeColor(s.Fg) == k.Fg, $"{where}: fg diverged");
                Assert.True(GridUpdateBuilder.EncodeColor(s.Bg) == k.Bg, $"{where}: bg diverged");
                Assert.True(s.Width == k.Width, $"{where}: width {s.Width} vs {k.Width}");
            }
        }
    }
}
