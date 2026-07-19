using System;

namespace Mainguard.Agents.Terminal;

/// <summary>
/// Pure VT/UTF-8 boundary detector. Given a byte buffer that begins on a clean boundary (the
/// streamer always feeds <c>carry + new</c>, and carry is whatever the previous call held back),
/// <see cref="SafeFlushLength"/> returns the length of the largest prefix that ends on <b>both</b>
/// a VT-sequence boundary and a UTF-8 codepoint boundary. Bytes past that prefix are an incomplete
/// escape sequence or a partial multi-byte codepoint and must be held for the next frame.
///
/// <para>The class is pure: no allocation beyond locals, no state retained between calls, and the
/// input span is never mutated. Splitting a stream at any offset and re-joining the safe prefix
/// with the held tail reassembles byte-identically and never emits a partial sequence — this is
/// the correctness heart of the terminal engine (invariant 2), exercised by the
/// split-at-every-offset corpus.</para>
/// </summary>
public sealed class VtBoundaryDetector
{
    private enum State
    {
        Ground,
        Esc,
        Csi,
        Osc,
        OscEsc,
        Dcs,
        DcsEsc,
        Ss3,
    }

    /// <summary>
    /// Returns the largest prefix length of <paramref name="buffer"/> that ends on a VT-sequence
    /// and UTF-8 codepoint boundary; bytes beyond it are held for the next frame.
    /// </summary>
    public int SafeFlushLength(ReadOnlySpan<byte> buffer)
    {
        var state = State.Ground;
        var utf8Remaining = 0;
        var lastSafe = 0;

        for (var i = 0; i < buffer.Length; i++)
        {
            var b = buffer[i];

            switch (state)
            {
                case State.Ground:
                    if (utf8Remaining > 0)
                    {
                        if (IsContinuation(b))
                        {
                            if (--utf8Remaining == 0)
                            {
                                lastSafe = i + 1; // completed a multi-byte codepoint
                            }
                        }
                        else
                        {
                            // Malformed: expected a continuation but got a fresh byte. Abandon the
                            // partial codepoint and re-evaluate this byte as a new unit so a bad
                            // stream can never hang the detector.
                            utf8Remaining = 0;
                            i--;
                        }

                        break;
                    }

                    if (b == Esc)
                    {
                        state = State.Esc;
                    }
                    else if (b < 0x80)
                    {
                        lastSafe = i + 1; // single-byte (ASCII / C0 control) codepoint
                    }
                    else if (b >= 0xC0 && b <= 0xDF)
                    {
                        utf8Remaining = 1;
                    }
                    else if (b >= 0xE0 && b <= 0xEF)
                    {
                        utf8Remaining = 2;
                    }
                    else if (b >= 0xF0 && b <= 0xF7)
                    {
                        utf8Remaining = 3;
                    }
                    else
                    {
                        // Stray continuation (0x80–0xBF) or invalid lead (0xF8–0xFF): treat as a
                        // self-contained byte so we neither split nor stall.
                        lastSafe = i + 1;
                    }

                    break;

                case State.Esc:
                    switch (b)
                    {
                        case (byte)'[':
                            state = State.Csi;
                            break;
                        case (byte)']':
                            state = State.Osc;
                            break;
                        case (byte)'P': // DCS
                        case (byte)'X': // SOS
                        case (byte)'^': // PM
                        case (byte)'_': // APC
                            state = State.Dcs;
                            break;
                        case (byte)'O':
                            state = State.Ss3;
                            break;
                        default:
                            if (b >= 0x20 && b <= 0x2F)
                            {
                                // Intermediate byte of a nF escape; stay in Esc until the final.
                            }
                            else
                            {
                                // Final byte of a two/three-byte escape (e.g. ESC c, ESC 7).
                                state = State.Ground;
                                lastSafe = i + 1;
                            }

                            break;
                    }

                    break;

                case State.Csi:
                    // Parameter (0x30–0x3F) and intermediate (0x20–0x2F) bytes stay in CSI; a final
                    // byte (0x40–0x7E) terminates it. Anything else is malformed and simply held
                    // (the streamer's 4 KB cap breaks a truly endless sequence).
                    if (b >= 0x40 && b <= 0x7E)
                    {
                        state = State.Ground;
                        lastSafe = i + 1;
                    }

                    break;

                case State.Osc:
                    // OSC ends on BEL or ST (ESC \) — both terminators are required by the corpus.
                    if (b == Bel)
                    {
                        state = State.Ground;
                        lastSafe = i + 1;
                    }
                    else if (b == Esc)
                    {
                        state = State.OscEsc;
                    }

                    break;

                case State.OscEsc:
                    if (b == (byte)'\\')
                    {
                        state = State.Ground;
                        lastSafe = i + 1;
                    }
                    else if (b == Esc)
                    {
                        // Another ESC — stay pending on a possible ST.
                    }
                    else
                    {
                        state = State.Osc;
                    }

                    break;

                case State.Dcs:
                    // DCS/SOS/PM/APC strings end only on ST (ESC \).
                    if (b == Esc)
                    {
                        state = State.DcsEsc;
                    }

                    break;

                case State.DcsEsc:
                    if (b == (byte)'\\')
                    {
                        state = State.Ground;
                        lastSafe = i + 1;
                    }
                    else if (b == Esc)
                    {
                        // Stay pending.
                    }
                    else
                    {
                        state = State.Dcs;
                    }

                    break;

                case State.Ss3:
                    // SS3 is exactly one following byte.
                    state = State.Ground;
                    lastSafe = i + 1;
                    break;
            }
        }

        return lastSafe;
    }

    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;
}
