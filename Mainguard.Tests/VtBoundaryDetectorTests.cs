using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Mainguard.Agents.Terminal;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// TI-P2-03 §1 / plan §6 row 1 + §5 invariant 2 — the correctness heart. The detector is pure and
/// exhaustively tested: every fixture sequence is split at <b>every</b> byte offset and the safe
/// prefix + held tail must reassemble byte-identically, never emitting a partial VT sequence or a
/// partial UTF-8 codepoint.
/// </summary>
public sealed class VtBoundaryDetectorTests
{
    private const byte ESC = 0x1B;
    private const byte BEL = 0x07;

    // Named corpus: CSI SGR, OSC 8 hyperlink with BOTH terminators, DCS, SS3, 2/3/4-byte UTF-8,
    // and a ZWJ emoji family — plus a combined stream mixing them all.
    public static IEnumerable<object[]> Corpus()
    {
        foreach (var (name, bytes) in CorpusEntries())
        {
            yield return new object[] { name, bytes };
        }
    }

    private static IEnumerable<(string Name, byte[] Bytes)> CorpusEntries()
    {
        yield return ("csi_sgr", Concat(Ascii("hi"), Bytes(ESC, (byte)'[', (byte)'1', (byte)';', (byte)'3', (byte)'1', (byte)'m'), Ascii("X")));
        yield return ("osc8_bel", Concat(
            Bytes(ESC, (byte)']'), Ascii("8;;https://example.com"), Bytes(BEL), Ascii("link"),
            Bytes(ESC, (byte)']'), Ascii("8;;"), Bytes(BEL)));
        yield return ("osc8_st", Concat(
            Bytes(ESC, (byte)']'), Ascii("8;;https://example.com"), Bytes(ESC, (byte)'\\'), Ascii("link"),
            Bytes(ESC, (byte)']'), Ascii("8;;"), Bytes(ESC, (byte)'\\')));
        yield return ("dcs", Concat(
            Bytes(ESC, (byte)'P'), Ascii("1$r0m"), Bytes(ESC, (byte)'\\')));
        yield return ("ss3", Bytes(ESC, (byte)'O', (byte)'P'));
        yield return ("utf8_2byte", Ascii("caf").Concat(Utf8("é")).ToArray());
        yield return ("utf8_3byte", Utf8("€10"));
        yield return ("utf8_4byte", Utf8("😀!"));
        yield return ("zwj_emoji", Utf8("👩‍👩‍👧‍👦"));
        yield return ("combined", Concat(
            Ascii("$ "), Bytes(ESC, (byte)'[', (byte)'3', (byte)'2', (byte)'m'), Utf8("café €"),
            Bytes(ESC, (byte)'[', (byte)'0', (byte)'m'), Utf8(" 😀 "),
            Bytes(ESC, (byte)']'), Ascii("0;title"), Bytes(BEL), Utf8("👩‍👩‍👧‍👦\n")));
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void SafeFlushLength_ShouldHoldTail_WhenSplitAtEveryOffset(string name, byte[] sequence)
    {
        _ = name;
        var detector = new VtBoundaryDetector();

        // A complete stream is fully flushable end-to-end.
        Assert.Equal(sequence.Length, detector.SafeFlushLength(sequence));

        for (var k = 0; k <= sequence.Length; k++)
        {
            var first = sequence.AsSpan(0, k);
            var safe1 = detector.SafeFlushLength(first);

            Assert.InRange(safe1, 0, k); // never claims bytes it was not given

            // Held tail = the bytes past the safe prefix.
            var carryLen = k - safe1;

            // Re-feed carry + the rest of the stream: it must now flush completely.
            var second = new byte[carryLen + (sequence.Length - k)];
            sequence.AsSpan(safe1, carryLen).CopyTo(second);
            sequence.AsSpan(k).CopyTo(second.AsSpan(carryLen));

            var safe2 = detector.SafeFlushLength(second);
            Assert.Equal(second.Length, safe2);

            // Reassembly is byte-identical: emitted prefix + everything the second call flushed.
            var reassembled = new byte[safe1 + safe2];
            sequence.AsSpan(0, safe1).CopyTo(reassembled);
            second.AsSpan(0, safe2).CopyTo(reassembled.AsSpan(safe1));
            Assert.Equal(sequence, reassembled);
        }
    }

    [Fact]
    public void SafeFlushLength_EmptyBuffer_ReturnsZero()
        => Assert.Equal(0, new VtBoundaryDetector().SafeFlushLength(ReadOnlySpan<byte>.Empty));

    [Fact]
    public void SafeFlushLength_PlainAscii_ReturnsFullLength()
    {
        var bytes = Ascii("plain text line");
        Assert.Equal(bytes.Length, new VtBoundaryDetector().SafeFlushLength(bytes));
    }

    [Fact]
    public void SafeFlushLength_IncompleteCsi_HoldsEntireTail()
    {
        // "ab" + ESC [ 1 ; — the CSI is unterminated, so only "ab" is safe.
        var bytes = Concat(Ascii("ab"), Bytes(ESC, (byte)'[', (byte)'1', (byte)';'));
        Assert.Equal(2, new VtBoundaryDetector().SafeFlushLength(bytes));
    }

    [Fact]
    public void SafeFlushLength_IncompleteUtf8_HoldsLeadByte()
    {
        // "x" + first two bytes of a 3-byte codepoint → only "x" is safe.
        var euro = Utf8("€");
        var bytes = Concat(Ascii("x"), new[] { euro[0], euro[1] });
        Assert.Equal(1, new VtBoundaryDetector().SafeFlushLength(bytes));
    }

    [Fact]
    public void SafeFlushLength_EndlessEscape_ReturnsZero()
    {
        // An OSC with no terminator can never be safely flushed — the streamer's cap breaks it.
        var bytes = Concat(Bytes(ESC, (byte)']'), Ascii(new string('x', 5000)));
        Assert.Equal(0, new VtBoundaryDetector().SafeFlushLength(bytes));
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static byte[] Bytes(params byte[] b) => b;

    private static byte[] Concat(params byte[][] arrays)
    {
        var total = arrays.Sum(a => a.Length);
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
