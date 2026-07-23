using System.Text;
using Avalonia.Input;
using Mainguard.Agents.UI.Controls;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// The P2-18 grid client's input encoder: keystrokes (incl. the DECCKM application-mode switch the
/// interim map lacked), the pinned paste semantics, and the mouse report encodings (SGR + legacy
/// X10) that close the two input-side coverage-matrix gaps.
/// </summary>
public sealed class GridInputEncoderTests
{
    private const string Esc = "\u001b";

    [Fact]
    public void Paste_ReusesThePinnedInterimSemantics()
    {
        // CR normalization + bracket wrapping are TerminalControl.BuildPasteBytes' pinned contract
        // (TerminalClipboardTests); the grid path must not fork them.
        Assert.Equal(
            $"{Esc}[200~line1\rline2{Esc}[201~",
            Encoding.UTF8.GetString(GridInputEncoder.EncodePaste("line1\r\nline2", bracketedPasteActive: true)!));
        Assert.Equal(
            "plain\rtext",
            Encoding.UTF8.GetString(GridInputEncoder.EncodePaste("plain\ntext", bracketedPasteActive: false)!));
        Assert.Null(GridInputEncoder.EncodePaste(null, bracketedPasteActive: true));
    }

    [Fact]
    public void MapKey_CtrlC_StaysSigint()
    {
        Assert.Equal(new byte[] { 0x03 }, GridInputEncoder.MapKey(Key.C, KeyModifiers.Control, cursorKeysApplication: false));
    }

    [Fact]
    public void MapKey_Arrows_FollowDecckm()
    {
        Assert.Equal($"{Esc}[A", Encoding.ASCII.GetString(GridInputEncoder.MapKey(Key.Up, KeyModifiers.None, false)!));
        Assert.Equal($"{Esc}OA", Encoding.ASCII.GetString(GridInputEncoder.MapKey(Key.Up, KeyModifiers.None, true)!));
        Assert.Equal($"{Esc}OD", Encoding.ASCII.GetString(GridInputEncoder.MapKey(Key.Left, KeyModifiers.None, true)!));
        Assert.Equal($"{Esc}OF", Encoding.ASCII.GetString(GridInputEncoder.MapKey(Key.End, KeyModifiers.None, true)!));

        // Modified arrows fall back to the base map regardless of DECCKM.
        Assert.Equal(
            Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Up, KeyModifiers.Shift) ?? new byte[0]),
            Encoding.ASCII.GetString(GridInputEncoder.MapKey(Key.Up, KeyModifiers.Shift, true) ?? new byte[0]));
    }

    [Fact]
    public void MapKey_ShiftTab_IsBackTab()
    {
        // The field bug: Shift+Tab degraded to a plain 0x09, so CLIs (Claude Code mode switch) never
        // saw the chord. Back-tab is CSI Z.
        Assert.Equal($"{Esc}[Z", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Tab, KeyModifiers.Shift)!));
        Assert.Equal($"{Esc}[Z", Encoding.ASCII.GetString(GridInputEncoder.MapKey(Key.Tab, KeyModifiers.Shift, true)!));
        Assert.Equal(new byte[] { 0x09 }, TerminalControl.MapKey(Key.Tab, KeyModifiers.None));
    }

    [Fact]
    public void MapKey_ShiftEnter_IsCsiU()
    {
        // The CSI-u form Claude Code's /terminal-setup configures ("newline without submit").
        Assert.Equal($"{Esc}[13;2u", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Enter, KeyModifiers.Shift)!));
        Assert.Equal(new byte[] { 0x0D }, TerminalControl.MapKey(Key.Enter, KeyModifiers.None));
    }

    [Fact]
    public void MapKey_AltChords_CarryTheEscMetaPrefix()
    {
        Assert.Equal(new byte[] { 0x1B, (byte)'b' }, TerminalControl.MapKey(Key.B, KeyModifiers.Alt));
        Assert.Equal(new byte[] { 0x1B, (byte)'F' }, TerminalControl.MapKey(Key.F, KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.Equal(new byte[] { 0x1B, (byte)'3' }, TerminalControl.MapKey(Key.D3, KeyModifiers.Alt));
        Assert.Equal(new byte[] { 0x1B, 0x0D }, TerminalControl.MapKey(Key.Enter, KeyModifiers.Alt));
        Assert.Equal(new byte[] { 0x1B, 0x7F }, TerminalControl.MapKey(Key.Back, KeyModifiers.Alt));
        // Ctrl+Alt+letter: ESC + the C0 byte.
        Assert.Equal(new byte[] { 0x1B, 0x03 }, TerminalControl.MapKey(Key.C, KeyModifiers.Control | KeyModifiers.Alt));
    }

    [Fact]
    public void MapKey_CtrlPunctuation_MapsToC0Controls()
    {
        Assert.Equal(new byte[] { 0x00 }, TerminalControl.MapKey(Key.Space, KeyModifiers.Control));
        Assert.Equal(new byte[] { 0x1B }, TerminalControl.MapKey(Key.OemOpenBrackets, KeyModifiers.Control));
        Assert.Equal(new byte[] { 0x1C }, TerminalControl.MapKey(Key.OemPipe, KeyModifiers.Control));
        Assert.Equal(new byte[] { 0x1D }, TerminalControl.MapKey(Key.OemCloseBrackets, KeyModifiers.Control));
        Assert.Equal(new byte[] { 0x1F }, TerminalControl.MapKey(Key.OemMinus, KeyModifiers.Control));
        Assert.Equal(new byte[] { 0x08 }, TerminalControl.MapKey(Key.Back, KeyModifiers.Control));
    }

    [Fact]
    public void MapKey_ModifiedSpecialKeys_UseTheXtermModifierParameter()
    {
        // mod = 1 + Shift·1 + Alt·2 + Ctrl·4.
        Assert.Equal($"{Esc}[1;2A", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Up, KeyModifiers.Shift)!));
        Assert.Equal($"{Esc}[1;5C", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Right, KeyModifiers.Control)!));
        Assert.Equal($"{Esc}[1;3D", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Left, KeyModifiers.Alt)!));
        Assert.Equal($"{Esc}[1;6H", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Home, KeyModifiers.Control | KeyModifiers.Shift)!));
        Assert.Equal($"{Esc}[3;5~", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.Delete, KeyModifiers.Control)!));
        Assert.Equal($"{Esc}[5;2~", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.PageUp, KeyModifiers.Shift)!));
        Assert.Equal($"{Esc}[1;2P", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.F1, KeyModifiers.Shift)!));
        Assert.Equal($"{Esc}[15;5~", Encoding.ASCII.GetString(TerminalControl.MapKey(Key.F5, KeyModifiers.Control)!));
        // Modified arrows stay CSI-form even in DECCKM application mode (xterm behavior).
        Assert.Equal($"{Esc}[1;5A", Encoding.ASCII.GetString(GridInputEncoder.MapKey(Key.Up, KeyModifiers.Control, cursorKeysApplication: true)!));
    }

    [Fact]
    public void Mouse_SgrEncoding_PressDragReleaseWheel()
    {
        Assert.Equal($"{Esc}[<0;5;3M", Encoding.ASCII.GetString(GridInputEncoder.EncodeMousePress(0, 5, 3, sgr: true)!));
        Assert.Equal($"{Esc}[<2;1;1M", Encoding.ASCII.GetString(GridInputEncoder.EncodeMousePress(2, 1, 1, sgr: true)!));
        Assert.Equal($"{Esc}[<0;5;3m", Encoding.ASCII.GetString(GridInputEncoder.EncodeMouseRelease(0, 5, 3, sgr: true)!));
        Assert.Equal($"{Esc}[<32;7;2M", Encoding.ASCII.GetString(GridInputEncoder.EncodeMouseDrag(0, 7, 2, sgr: true)!));
        Assert.Equal($"{Esc}[<64;4;4M", Encoding.ASCII.GetString(GridInputEncoder.EncodeWheel(up: true, 4, 4, sgr: true)!));
        Assert.Equal($"{Esc}[<65;4;4M", Encoding.ASCII.GetString(GridInputEncoder.EncodeWheel(up: false, 4, 4, sgr: true)!));
    }

    [Fact]
    public void Mouse_LegacyX10Encoding_PlusThirtyTwoBytes()
    {
        // Left press at (col 5, row 3): ESC [ M, Cb=0+32, Cx=5+32, Cy=3+32.
        Assert.Equal(
            new byte[] { 0x1B, (byte)'[', (byte)'M', 32, 37, 35 },
            GridInputEncoder.EncodeMousePress(0, 5, 3, sgr: false));

        // X10 release is always code 3.
        Assert.Equal(
            new byte[] { 0x1B, (byte)'[', (byte)'M', 35, 37, 35 },
            GridInputEncoder.EncodeMouseRelease(0, 5, 3, sgr: false));

        // Coordinates beyond the X10 encodable range are capped, never wrapped/overflowed.
        var far = GridInputEncoder.EncodeMousePress(0, 500, 500, sgr: false)!;
        Assert.Equal(223 + 32, far[4]);
        Assert.Equal(223 + 32, far[5]);
    }

    [Fact]
    public void Mouse_InvalidInputs_ReturnNull()
    {
        Assert.Null(GridInputEncoder.EncodeMousePress(5, 1, 1, sgr: true));  // unknown button
        Assert.Null(GridInputEncoder.EncodeMousePress(0, 0, 1, sgr: true));  // 1-based coords
    }
}
