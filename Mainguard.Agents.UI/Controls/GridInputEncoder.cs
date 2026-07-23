using System;
using System.Text;
using Avalonia.Input;

namespace Mainguard.Agents.UI.Controls;

/// <summary>
/// The P2-18 grid engine's client-side input encoder: keystrokes, paste, and mouse reports become
/// the byte sequences a terminal sends toward the PTY. Pure and Avalonia-render-free so the P2-04
/// coverage matrix drives it directly (the bracketed-paste and mouse-reporting rows the interim
/// engine failed). Mode-awareness (bracketed paste, DECCKM, mouse protocol) comes from the
/// server-authoritative <see cref="GridModel.Modes"/> — the daemon's vterm/DECSET tracker owns the
/// truth; this type only encodes.
///
/// <para>Paste reuses the interim engine's pinned <see cref="TerminalControl.BuildPasteBytes"/>
/// (CR normalization + 200~/201~ wrapping) so <c>TerminalClipboardTests</c> semantics carry over
/// unchanged. Ctrl+C stays SIGINT (0x03) via <see cref="TerminalControl.MapKey"/>.</para>
/// </summary>
internal static class GridInputEncoder
{
    /// <summary>Paste bytes: CR-normalized, bracket-wrapped when the CLI enabled DECSET 2004.</summary>
    public static byte[]? EncodePaste(string? text, bool bracketedPasteActive)
        => TerminalControl.BuildPasteBytes(text, bracketedPasteActive);

    /// <summary>
    /// Keystroke bytes. Delegates to the interim engine's tested map, then applies DECCKM: in
    /// application cursor-key mode the arrows/Home/End encode SS3 (<c>ESC O A</c>…) instead of CSI.
    /// </summary>
    public static byte[]? MapKey(Key key, KeyModifiers modifiers, bool cursorKeysApplication)
    {
        if (cursorKeysApplication && modifiers == KeyModifiers.None)
        {
            var ss3 = key switch
            {
                Key.Up => "OA",
                Key.Down => "OB",
                Key.Right => "OC",
                Key.Left => "OD",
                Key.Home => "OH",
                Key.End => "OF",
                _ => null,
            };
            if (ss3 is not null)
            {
                return Esc(ss3);
            }
        }

        return TerminalControl.MapKey(key, modifiers);
    }

    /// <summary>Mouse press report for 1-based cell (<paramref name="col"/>, <paramref name="row"/>).
    /// Button 0/1/2 = left/middle/right. SGR (DECSET 1006): <c>ESC[&lt;b;col;rowM</c>; legacy X10
    /// otherwise (coordinates capped at 223).</summary>
    public static byte[]? EncodeMousePress(int button, int col, int row, bool sgr)
        => EncodeMouse(button, col, row, sgr, press: true);

    /// <summary>Mouse release report. SGR encodes the true button with a final <c>m</c>; X10 uses
    /// the release code 3.</summary>
    public static byte[]? EncodeMouseRelease(int button, int col, int row, bool sgr)
        => EncodeMouse(button, col, row, sgr, press: false);

    /// <summary>Mouse drag/motion report (button held): the button code + 32.</summary>
    public static byte[]? EncodeMouseDrag(int button, int col, int row, bool sgr)
        => EncodeMouseCode(button + 32, col, row, sgr, release: false);

    /// <summary>Wheel report: button 64 (up) / 65 (down).</summary>
    public static byte[]? EncodeWheel(bool up, int col, int row, bool sgr)
        => EncodeMouseCode(up ? 64 : 65, col, row, sgr, release: false);

    private static byte[]? EncodeMouse(int button, int col, int row, bool sgr, bool press)
    {
        if (button is < 0 or > 2)
        {
            return null;
        }

        if (sgr)
        {
            return EncodeMouseCode(button, col, row, sgr: true, release: !press);
        }

        return EncodeMouseCode(press ? button : 3, col, row, sgr: false, release: false);
    }

    private static byte[]? EncodeMouseCode(int code, int col, int row, bool sgr, bool release)
    {
        if (col < 1 || row < 1)
        {
            return null;
        }

        if (sgr)
        {
            return Encoding.ASCII.GetBytes(
                $"\u001b[<{code};{col};{row}{(release ? 'm' : 'M')}");
        }

        // Legacy X10 bytes: ESC [ M Cb Cx Cy with each value + 32, capped at the encodable range.
        var cb = (byte)Math.Min(code + 32, 255);
        var cx = (byte)(Math.Min(col, 223) + 32);
        var cy = (byte)(Math.Min(row, 223) + 32);
        return new byte[] { 0x1B, (byte)'[', (byte)'M', cb, cx, cy };
    }

    private static byte[] Esc(string tail)
    {
        var bytes = new byte[tail.Length + 1];
        bytes[0] = 0x1B;
        Encoding.ASCII.GetBytes(tail, 0, tail.Length, bytes, 1);
        return bytes;
    }
}
