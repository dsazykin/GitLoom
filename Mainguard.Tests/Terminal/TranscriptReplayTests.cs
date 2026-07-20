using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mainguard.Agents.Terminal;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// P2-04 §3.3 golden-transcript replay. Committed <c>&lt;name&gt;.bytes</c> streams are replayed
/// through the shared <see cref="ITerminalEngineHarness"/> and the resulting grid is compared
/// <b>cell-by-cell</b> against the committed <c>&lt;name&gt;.golden</c>. Replay is byte-order-only:
/// no timestamps are read and there are no sleeps anywhere in the comparison path (rejection
/// trigger). Determinism is enforced two ways — regeneration must be byte-identical, and feeding the
/// same bytes in seeded-random chunks (optionally re-joined through <see cref="VtBoundaryDetector"/>)
/// must produce the same grid as a one-shot feed.
///
/// <para>The goldens capture whatever the <b>interim</b> engine currently produces (a regression
/// snapshot — it mishandles alt-screen/truecolor); true conformance lives in the coverage matrix.
/// The <c>vim/htop/tmux</c> streams are representative captures and the <c>claude-code/opencode</c>
/// streams are <b>synthetic</b> (those CLIs are proprietary and unavailable here) — see
/// <c>Transcripts/README.md</c>. Every stream replays identically regardless of origin.</para>
/// </summary>
public sealed class TranscriptReplayTests
{
    /// <summary>The five transcripts and their fixed grid size (fixed size ⇒ deterministic golden).</summary>
    public static readonly IReadOnlyList<(string Name, int Cols, int Rows)> Transcripts = new[]
    {
        ("claude-code", 80, 24),
        ("opencode", 80, 24),
        ("vim", 80, 24),
        ("htop-60s", 80, 24),
        ("tmux", 80, 24),
    };

    public static IEnumerable<object[]> TranscriptNames()
    {
        foreach (var t in Transcripts)
        {
            yield return new object[] { t.Name, t.Cols, t.Rows };
        }
    }

    [Theory]
    [MemberData(nameof(TranscriptNames))]
    public void TranscriptReplay(string name, int cols, int rows)
    {
        var bytes = ReadBytes(name);
        var actual = ReplayOneShot(bytes, cols, rows);

        var goldenPath = GoldenPath(name);
        if (TerminalHarnessPaths.RegenGoldens)
        {
            WriteGolden(goldenPath, actual.Serialize());
            return;
        }

        Assert.True(File.Exists(goldenPath), $"Missing golden for '{name}'. Regenerate with MAINGUARD_REGEN_GOLDENS=1.");
        var expected = ReadGolden(goldenPath);
        Assert.True(
            expected == actual.Serialize(),
            $"Replay of '{name}' diverged from its golden:\n{DescribeSerializedDiff(expected, actual.Serialize())}");
    }

    /// <summary>
    /// Contract #4: feeding the bytes in seeded-random chunks — re-joined through the boundary
    /// detector so no VT sequence or codepoint is ever split — yields the same grid as one shot.
    /// </summary>
    [Theory]
    [MemberData(nameof(TranscriptNames))]
    public void TranscriptReplay_ChunkedFeeds_Identical(string name, int cols, int rows)
    {
        var bytes = ReadBytes(name);
        var oneShot = ReplayOneShot(bytes, cols, rows).Serialize();

        // A few different seeds to shake out any offset-dependent parser state.
        foreach (var seed in new[] { 1, 7, 42, 1337 })
        {
            var chunked = ReplayChunked(bytes, cols, rows, seed).Serialize();
            Assert.True(
                oneShot == chunked,
                $"'{name}' chunked feed (seed {seed}) diverged from the one-shot feed:\n"
                + DescribeSerializedDiff(oneShot, chunked));
        }
    }

    /// <summary>Contract #6: regenerating every golden in memory equals the committed bytes exactly.</summary>
    [Fact]
    public void Goldens_RegenIsByteIdentical()
    {
        foreach (var (name, cols, rows) in Transcripts)
        {
            var goldenPath = GoldenPath(name);
            Assert.True(File.Exists(goldenPath), $"Missing golden for '{name}'.");

            var regenerated = ReplayOneShot(ReadBytes(name), cols, rows).Serialize();
            var committed = ReadGolden(goldenPath);
            Assert.True(
                committed == regenerated,
                $"Golden for '{name}' is not byte-identical to a fresh regeneration "
                + "(determinism invariant). If this is an intentional engine change, regenerate with "
                + $"MAINGUARD_REGEN_GOLDENS=1.\n{DescribeSerializedDiff(committed, regenerated)}");
        }
    }

    /// <summary>Demonstrates the abstraction seam (contract #5): the harness drives an engine purely
    /// through <see cref="ITerminalEngineHarness"/>; P2-18 adds a second implementation unchanged.</summary>
    [Fact]
    public void Harness_DrivesEngine_ThroughGridReadback()
    {
        ITerminalEngineHarness engine = new InterimEngineHarness();
        engine.Reset(80, 24);
        engine.Feed(ReadBytes("vim"));
        var grid = engine.ReadGrid();
        Assert.Equal(80, grid.Cols);
        Assert.Equal(24, grid.Rows);
        Assert.Equal("interim", engine.EngineName);
    }

    private static GridSnapshot ReplayOneShot(byte[] bytes, int cols, int rows)
    {
        var engine = new InterimEngineHarness();
        engine.Reset(cols, rows);
        engine.Feed(bytes);
        return engine.ReadGrid();
    }

    private static GridSnapshot ReplayChunked(byte[] bytes, int cols, int rows, int seed)
    {
        var engine = new InterimEngineHarness();
        engine.Reset(cols, rows);

        var detector = new VtBoundaryDetector();
        var rng = new Random(seed);
        var carry = new List<byte>();
        var offset = 0;

        while (offset < bytes.Length)
        {
            var take = Math.Min(rng.Next(1, 17), bytes.Length - offset);
            for (var i = 0; i < take; i++)
            {
                carry.Add(bytes[offset + i]);
            }

            offset += take;

            // Only feed the prefix that ends on a clean VT + UTF-8 boundary; hold the rest.
            var carryArray = carry.ToArray();
            var safe = detector.SafeFlushLength(carryArray);
            if (safe > 0)
            {
                engine.Feed(carryArray.AsSpan(0, safe));
                carry.RemoveRange(0, safe);
            }
        }

        if (carry.Count > 0)
        {
            engine.Feed(carry.ToArray()); // flush any tail at end-of-stream
        }

        return engine.ReadGrid();
    }

    private static byte[] ReadBytes(string name)
    {
        var path = Path.Combine(TerminalHarnessPaths.TranscriptsDir, name + ".bytes");
        Assert.True(File.Exists(path), $"Missing transcript bytes for '{name}' at {path}.");
        return File.ReadAllBytes(path);
    }

    private static string GoldenPath(string name) =>
        Path.Combine(TerminalHarnessPaths.TranscriptsDir, name + ".golden");

    // Goldens are LF-locked text; read/write with '\n' preserved and no BOM.
    private static string ReadGolden(string path) =>
        File.ReadAllText(path, new UTF8Encoding(false)).Replace("\r\n", "\n");

    private static void WriteGolden(string path, string content) =>
        File.WriteAllText(path, content, new UTF8Encoding(false));

    private static string DescribeSerializedDiff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var sb = new StringBuilder();
        var max = Math.Max(e.Length, a.Length);
        var shown = 0;
        for (var i = 0; i < max && shown < 30; i++)
        {
            var el = i < e.Length ? e[i] : "<none>";
            var al = i < a.Length ? a[i] : "<none>";
            if (el != al)
            {
                sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"  line {i}: golden='{el}' actual='{al}'\n");
                shown++;
            }
        }

        if (shown == 0)
        {
            sb.Append("  (no line-level differences — check trailing newline/whitespace)\n");
        }

        return sb.ToString();
    }
}
