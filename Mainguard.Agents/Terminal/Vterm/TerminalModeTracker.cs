using System;
using System.Text;

namespace Mainguard.Agents.Terminal.Vterm;

/// <summary>
/// A pure streaming scanner over the PTY output byte stream for the terminal state libvterm
/// tracks internally but does not surface through screen callbacks:
///
/// <list type="bullet">
/// <item>DECSET/DECRST 2004 (bracketed paste — the client must wrap pastes in ESC[200~/201~),</item>
/// <item>DECSET/DECRST 1 (DECCKM — arrows encode SS3 vs CSI),</item>
/// <item>DECSET/DECRST 1006 (SGR mouse protocol — the report encoding, distinct from the
/// on/off mouse mode vterm reports via VTERM_PROP_MOUSE),</item>
/// <item>OSC 52 clipboard SETs (decoded and raised as <see cref="ClipboardCopyRequested"/>).
/// A query payload ("?") is NEVER answered or raised — answering would hand the host clipboard
/// to the jailed CLI. This is the daemon-side home of the rule the interim engine enforced in
/// <c>VtScreen</c>; <c>TerminalClipboardTests</c> pins the same behaviour there.</item>
/// </list>
///
/// The scanner is stateful across <see cref="Feed"/> calls (sequences may split at frame
/// boundaries — the 4 KB holdback cap can force a mid-sequence flush), allocation-light in the
/// ground state, and bounded: an endless OSC payload caps at <see cref="OscCaptureCap"/> and is
/// discarded whole. It never interprets grid content — that is libvterm's job.
/// </summary>
public sealed class TerminalModeTracker
{
    private const byte Esc = 0x1B;
    private const byte Bel = 0x07;
    private const int OscCaptureCap = 100_000;
    private const int CsiParamCap = 64;

    private enum State
    {
        Ground,
        AfterEsc,
        Csi,
        Osc,
        OscEsc,
        ConsumeString,     // DCS/SOS/PM/APC: consume to ST, never capture
        ConsumeStringEsc,
    }

    private State _state = State.Ground;
    private readonly StringBuilder _csi = new();
    private readonly StringBuilder _osc = new();
    private bool _oscOverflow;

    /// <summary>DECSET 2004 — pasted input should be bracket-wrapped.</summary>
    public bool BracketedPaste { get; private set; }

    /// <summary>DECCKM (DECSET 1) — cursor keys encode application (SS3) sequences.</summary>
    public bool CursorKeysApplication { get; private set; }

    /// <summary>DECSET 1006 — mouse reports use the SGR encoding.</summary>
    public bool MouseSgr { get; private set; }

    /// <summary>The application requested a clipboard write via OSC 52 — the decoded text.</summary>
    public event Action<string>? ClipboardCopyRequested;

    public void Feed(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            Process(b);
        }
    }

    /// <summary>Full reset (ESC c / session restart): private modes clear.</summary>
    public void Reset()
    {
        BracketedPaste = false;
        CursorKeysApplication = false;
        MouseSgr = false;
        _state = State.Ground;
        _csi.Clear();
        _osc.Clear();
        _oscOverflow = false;
    }

    private void Process(byte b)
    {
        switch (_state)
        {
            case State.Ground:
                if (b == Esc)
                {
                    _state = State.AfterEsc;
                }

                break;

            case State.AfterEsc:
                switch (b)
                {
                    case (byte)'[':
                        _csi.Clear();
                        _state = State.Csi;
                        break;
                    case (byte)']':
                        _osc.Clear();
                        _oscOverflow = false;
                        _state = State.Osc;
                        break;
                    case (byte)'P':
                    case (byte)'X':
                    case (byte)'^':
                    case (byte)'_':
                        _state = State.ConsumeString;
                        break;
                    case (byte)'c':
                        Reset();
                        break;
                    default:
                        _state = State.Ground;
                        break;
                }

                break;

            case State.Csi:
                if (b is >= (byte)'0' and <= (byte)'9' or (byte)';' or (byte)'?' or (byte)':')
                {
                    if (_csi.Length < CsiParamCap)
                    {
                        _csi.Append((char)b);
                    }
                }
                else if (b is >= 0x40 and <= 0x7E)
                {
                    ApplyCsi((char)b, _csi.ToString());
                    _state = State.Ground;
                }
                else if (b is >= 0x20 and <= 0x2F)
                {
                    // Intermediate byte — keep consuming to the final.
                }
                else if (b == Esc)
                {
                    _state = State.AfterEsc; // aborted sequence
                }
                else if (b >= 0x20)
                {
                    _state = State.Ground; // malformed — bail out
                }

                break;

            case State.Osc:
                if (b == Bel)
                {
                    CompleteOsc();
                    _state = State.Ground;
                }
                else if (b == Esc)
                {
                    _state = State.OscEsc;
                }
                else if (_osc.Length < OscCaptureCap)
                {
                    _osc.Append((char)b);
                }
                else
                {
                    _oscOverflow = true;
                }

                break;

            case State.OscEsc:
                if (b == (byte)'\\')
                {
                    CompleteOsc();
                    _state = State.Ground;
                }
                else
                {
                    // Not ST — the ESC belongs to the payload stream; keep consuming.
                    _state = State.Osc;
                }

                break;

            case State.ConsumeString:
                if (b == Esc)
                {
                    _state = State.ConsumeStringEsc;
                }
                else if (b == Bel)
                {
                    _state = State.Ground;
                }

                break;

            case State.ConsumeStringEsc:
                _state = b == (byte)'\\' ? State.Ground : State.ConsumeString;
                break;
        }
    }

    private void ApplyCsi(char final, string parameters)
    {
        if (final is not ('h' or 'l') || !parameters.StartsWith('?'))
        {
            return;
        }

        var on = final == 'h';
        foreach (var part in parameters[1..].Split(';'))
        {
            switch (part)
            {
                case "1":
                    CursorKeysApplication = on;
                    break;
                case "1006":
                    MouseSgr = on;
                    break;
                case "2004":
                    BracketedPaste = on;
                    break;
            }
        }
    }

    private void CompleteOsc()
    {
        var overflowed = _oscOverflow;
        _oscOverflow = false;
        var payload = _osc.ToString();
        _osc.Clear();

        if (overflowed || !payload.StartsWith("52;", StringComparison.Ordinal))
        {
            return;
        }

        // OSC 52 ; <selection targets> ; <base64 data>. "?" is a clipboard QUERY — never answered.
        var dataStart = payload.IndexOf(';', 3);
        if (dataStart < 0)
        {
            return;
        }

        var data = payload[(dataStart + 1)..];
        if (data.Length == 0 || data == "?")
        {
            return;
        }

        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(data));
            if (text.Length > 0)
            {
                ClipboardCopyRequested?.Invoke(text);
            }
        }
        catch (FormatException)
        {
            // Not valid base64 — malformed or truncated; nothing to copy.
        }
    }
}
