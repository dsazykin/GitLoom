using System.Collections.Generic;
using System.Text;
using Avalonia.Input;
using Mainguard.Agents.UI.Controls;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// Terminal ↔ host clipboard (field gap 2026-07-22: claude-code's login screen said "copied" but the
/// host clipboard stayed empty, and there was no paste path for the login code at all).
/// Copy OUT is application-driven via OSC 52 — <see cref="VtScreen"/> decodes it and raises
/// <c>ClipboardCopyRequested</c> (the control puts it on the host clipboard); a "?" query is never
/// answered (that would leak the host clipboard into the jail). Paste IN is the three terminal paste
/// chords building CR-normalized bytes, bracketed (DECSET 2004) when the CLI asked for it.
/// </summary>
public class TerminalClipboardTests
{
    private const string Esc = "\u001b";
    private const string Bel = "\u0007";
    private const string St = "\u001b\\";

    private static (VtScreen Screen, List<string> Copies) NewScreen()
    {
        var screen = new VtScreen(80, 24);
        var copies = new List<string>();
        screen.ClipboardCopyRequested += copies.Add;
        return (screen, copies);
    }

    private static void Feed(VtScreen screen, string text) => screen.Feed(Encoding.UTF8.GetBytes(text));

    private static string Osc52(string text, string? terminator = null) =>
        $"{Esc}]52;c;{System.Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}{terminator ?? Bel}";

    [Fact]
    public void Osc52_BelTerminated_RaisesTheDecodedCopy()
    {
        var (screen, copies) = NewScreen();
        Feed(screen, Osc52("https://claude.com/cai/oauth/authorize?code=true"));
        Assert.Equal(new[] { "https://claude.com/cai/oauth/authorize?code=true" }, copies);
    }

    [Fact]
    public void Osc52_StTerminated_RaisesTheDecodedCopy()
    {
        var (screen, copies) = NewScreen();
        Feed(screen, Osc52("hello world", terminator: St));
        Assert.Equal(new[] { "hello world" }, copies);
    }

    [Fact]
    public void Osc52_SplitAcrossFeeds_StillDecodes()
    {
        var (screen, copies) = NewScreen();
        var sequence = Osc52("split-payload");
        Feed(screen, sequence[..10]);
        Feed(screen, sequence[10..]);
        Assert.Equal(new[] { "split-payload" }, copies);
    }

    [Fact]
    public void Osc52_Query_IsNeverAnswered_AndNeverRaises()
    {
        // "?" asks the terminal to SEND the clipboard — answering would hand the host clipboard
        // to the jailed CLI, so it is ignored outright.
        var (screen, copies) = NewScreen();
        Feed(screen, $"{Esc}]52;c;?{Bel}");
        Assert.Empty(copies);
    }

    [Fact]
    public void Osc52_InvalidBase64_IsIgnored()
    {
        var (screen, copies) = NewScreen();
        Feed(screen, $"{Esc}]52;c;!!!not-base64!!!{Bel}");
        Assert.Empty(copies);
    }

    [Fact]
    public void OtherOscStrings_TitlesAndLinks_NeverRaise()
    {
        var (screen, copies) = NewScreen();
        Feed(screen, $"{Esc}]0;window title{Bel}{Esc}]8;;https://example.com{St}text{Esc}]8;;{St}");
        Assert.Empty(copies);
    }

    [Fact]
    public void BracketedPaste_TracksDecset2004()
    {
        var (screen, _) = NewScreen();
        Assert.False(screen.BracketedPaste);

        Feed(screen, $"{Esc}[?2004h");
        Assert.True(screen.BracketedPaste);

        Feed(screen, $"{Esc}[?2004l");
        Assert.False(screen.BracketedPaste);

        // ESC c full reset clears private modes too.
        Feed(screen, $"{Esc}[?2004h{Esc}c");
        Assert.False(screen.BracketedPaste);
    }

    [Fact]
    public void BuildPasteBytes_NormalizesNewlinesToCr()
    {
        var bytes = TerminalControl.BuildPasteBytes("line1\r\nline2\nline3", bracketedPaste: false);
        Assert.Equal("line1\rline2\rline3", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void BuildPasteBytes_WrapsInBracketMarkers_WhenTheCliAskedForThem()
    {
        var bytes = TerminalControl.BuildPasteBytes("code-123", bracketedPaste: true);
        Assert.Equal($"{Esc}[200~code-123{Esc}[201~", Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void BuildPasteBytes_NothingToPaste_IsNull()
    {
        Assert.Null(TerminalControl.BuildPasteBytes(null, bracketedPaste: false));
        Assert.Null(TerminalControl.BuildPasteBytes("", bracketedPaste: true));
    }

    [Fact]
    public void PasteChords_AreTheThreeTerminalConventions_AndCtrlCIsNotOne()
    {
        Assert.True(TerminalControl.IsPasteChord(Key.V, KeyModifiers.Control));
        Assert.True(TerminalControl.IsPasteChord(Key.V, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.True(TerminalControl.IsPasteChord(Key.Insert, KeyModifiers.Shift));

        Assert.False(TerminalControl.IsPasteChord(Key.C, KeyModifiers.Control)); // stays SIGINT
        Assert.False(TerminalControl.IsPasteChord(Key.V, KeyModifiers.None));    // plain 'v' types
        Assert.False(TerminalControl.IsPasteChord(Key.Insert, KeyModifiers.Control));
    }
}
