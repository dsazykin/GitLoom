using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Server.Terminal;
using Xunit;

namespace GitLoom.Server.Tests;

/// <summary>
/// TI-P2-03 §5–6 / plan §6 rows 2, 7, 9 — the streamer batches PTY bytes into frames on the flush
/// cadence, never splits a VT sequence or UTF-8 codepoint, flushes at the 4 KB holdback cap for a
/// malformed endless escape, and keeps pooled memory flat under a firehose.
/// </summary>
public sealed class TerminalStreamerTests
{
    private const byte ESC = 0x1B;

    [Fact]
    public void Ingest_BurstWithinTick_ShouldDrainAsOneFrame()
    {
        using var streamer = new TerminalStreamer();

        // Several writes accumulate between ticks; one drain emits them as a single frame.
        streamer.Ingest(Encoding.ASCII.GetBytes("foo "));
        streamer.Ingest(Encoding.ASCII.GetBytes("bar "));
        streamer.Ingest(Encoding.ASCII.GetBytes("baz"));

        Assert.True(streamer.TryDrain(out var frame));
        Assert.Equal("foo bar baz", Encoding.ASCII.GetString(frame));
        Assert.False(streamer.TryDrain(out _)); // nothing left this tick
    }

    [Fact]
    public void TryDrain_MalformedEndlessEscape_ShouldFlushAtHoldbackCap()
    {
        using var streamer = new TerminalStreamer(holdbackCap: 4096);

        // 5 KB of OSC-start garbage with no terminator — never a boundary.
        var garbage = new byte[5000];
        garbage[0] = ESC;
        garbage[1] = (byte)']';
        for (var i = 2; i < garbage.Length; i++)
        {
            garbage[i] = (byte)'x';
        }

        streamer.Ingest(garbage);

        // Past the cap, the streamer flushes regardless rather than buffering unboundedly.
        Assert.True(streamer.TryDrain(out var frame));
        Assert.Equal(garbage.Length, frame.Length);
    }

    [Fact]
    public void TryDrain_UnterminatedSequence_ShouldNotSplitAcrossFrames()
    {
        using var streamer = new TerminalStreamer();

        // Chunk ends mid-CSI: "AB" + ESC [ — only "AB" is safe.
        streamer.Ingest(new byte[] { (byte)'A', (byte)'B', ESC, (byte)'[' });
        Assert.True(streamer.TryDrain(out var frame1));
        Assert.Equal("AB", Encoding.ASCII.GetString(frame1));
        Assert.DoesNotContain(ESC, frame1); // never emits a partial sequence

        // The rest of the sequence arrives; now it flushes intact.
        streamer.Ingest(new byte[] { (byte)'1', (byte)'m' });
        Assert.True(streamer.TryDrain(out var frame2));
        Assert.Equal(new byte[] { ESC, (byte)'[', (byte)'1', (byte)'m' }, frame2);
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void Firehose_100Mb_ShouldKeepPooledMemoryFlat_AndLoseNoBytes()
    {
        using var streamer = new TerminalStreamer();

        var chunk = new byte[64 * 1024];
        for (var i = 0; i < chunk.Length; i++)
        {
            chunk[i] = (byte)(i % 2 == 0 ? 'y' : '\n');
        }

        long ingested = 0;
        long emitted = 0;
        const long target = 100L * 1024 * 1024;

        while (ingested < target)
        {
            streamer.Ingest(chunk);
            ingested += chunk.Length;
            while (streamer.TryDrain(out var frame))
            {
                emitted += frame.Length;
            }
        }

        Assert.Equal(ingested, emitted);         // no bytes lost through 100 MB
        Assert.True(streamer.PendingBytes < 4096); // carry stays bounded
        Assert.True(streamer.PooledHighWaterBytes <= 256 * 1024,
            $"pooled high-water was {streamer.PooledHighWaterBytes} bytes");
    }

    [Fact]
    public async Task RunAsync_ShouldStreamStreamContents_OnBoundariesOnly()
    {
        // A scripted stream mixing text, a CSI SGR, an emoji, and a trailing incomplete-then-complete run.
        var source = Concat(
            Encoding.ASCII.GetBytes("$ "),
            new byte[] { ESC, (byte)'[', (byte)'3', (byte)'2', (byte)'m' },
            Encoding.UTF8.GetBytes("ok 😀\n"),
            new byte[] { ESC, (byte)'[', (byte)'0', (byte)'m' });

        using var streamer = new TerminalStreamer();
        var frames = new List<byte[]>();

        await streamer.RunAsync(
            new MemoryStream(source),
            (frame, _) =>
            {
                frames.Add(frame.ToArray());
                return Task.CompletedTask;
            },
            flushInterval: TimeSpan.FromMilliseconds(5),
            ct: CancellationToken.None);

        var reassembled = new List<byte>();
        foreach (var f in frames)
        {
            reassembled.AddRange(f);
        }

        Assert.Equal(source, reassembled.ToArray());
    }

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = 0;
        foreach (var a in arrays)
        {
            total += a.Length;
        }

        var result = new byte[total];
        var offset = 0;
        foreach (var a in arrays)
        {
            a.CopyTo(result, offset);
            offset += a.Length;
        }

        return result;
    }
}
