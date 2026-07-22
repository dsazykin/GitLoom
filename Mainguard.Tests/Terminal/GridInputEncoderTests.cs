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
