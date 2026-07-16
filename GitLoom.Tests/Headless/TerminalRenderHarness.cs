using System;
using System.IO;
using System.Text;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Controls;
using GitLoom.App.Theming;
using Xunit;

namespace GitLoom.Tests.Headless;

// TI-P2-03 §8 (v1 A.6 pattern) — renders a coloured TUI frame through the interim terminal engine
// (TerminalControl over the pure VtScreen) offscreen in two themes so the ANSI cell palette and its
// legibility can be inspected without a display. Interactive terminal *feel* (vim/htop/tmux latency,
// reflow, scroll) stays a manual matrix — the v1 boundary. Captures a PNG per theme to
// artifacts_headless/ (visual review, not pass/fail).
public class TerminalRenderHarness
{
    // ESC, kept as a char constant so no raw control byte lives in the source.
    private const char Esc = '\u001b';

    [AvaloniaFact]
    public void Capture_TerminalFrame_DarkAndLight()
    {
        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
            CaptureOnce("terminal_frame_MidnightLoom.png");

            ThemeManager.Apply("DaylightLoom", persist: false);
            CaptureOnce("terminal_frame_DaylightLoom.png");
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    private static void CaptureOnce(string fileName)
    {
        var terminal = new TerminalControl();
        var win = new Window { Content = terminal, Width = 720, Height = 470 };
        win.Show();

        terminal.FeedOutput(BuildColoredFrame());
        for (var i = 0; i < 10; i++)
        {
            Pump();
        }

        var frame = win.CaptureRenderedFrame();
        frame?.Save(Path.Combine(ArtifactsDir(), fileName));

        // Sanity: the engine actually parsed the frame into the grid.
        var grid = terminal.ReadGrid();
        Assert.Contains("GitLoom", grid.RowText(0));
        HarnessHygiene.Teardown(win);
    }

    private static byte[] BuildColoredFrame()
    {
        // A small htop-ish frame: a bold title bar, coloured usage bars, and a git status panel —
        // exercising bold, the base ANSI colours, and box-drawing glyphs. Sgr(code) = ESC [ code.
        static string Sgr(string code) => Esc + "[" + code;
        var reset = Sgr("0m");
        var sb = new StringBuilder();

        sb.Append(Sgr("1;36m")).Append(" GitLoom Terminal — interim PTY engine (P2-03)").Append(reset).Append("\r\n\r\n");

        sb.Append("  CPU0 ").Append(Sgr("32m")).Append("|||||||||||||").Append(reset).Append("            23%\r\n");
        sb.Append("  CPU1 ").Append(Sgr("33m")).Append("||||||||||||||||||||||").Append(reset).Append("     61%\r\n");
        sb.Append("  CPU2 ").Append(Sgr("31m")).Append("||||||||||||||||||||||||||||||").Append(reset).Append(" 94%\r\n\r\n");

        sb.Append("  ").Append(Sgr("34m")).Append("┌───────────── git status ─────────────┐").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("│").Append(reset)
          .Append(" branch  ").Append(Sgr("1;35m")).Append("feature/P2-03-terminal").Append(reset)
          .Append("        ").Append(Sgr("34m")).Append("│").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("│").Append(reset)
          .Append(" ").Append(Sgr("32m")).Append("● staged   PtyProcessShim.cs").Append(reset)
          .Append("          ").Append(Sgr("34m")).Append("│").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("│").Append(reset)
          .Append(" ").Append(Sgr("31m")).Append("● modified VtBoundaryDetector.cs").Append(reset)
          .Append("      ").Append(Sgr("34m")).Append("│").Append(reset).Append("\r\n");
        sb.Append("  ").Append(Sgr("34m")).Append("└──────────────────────────────────────┘").Append(reset).Append("\r\n\r\n");

        sb.Append("  ").Append(Sgr("90m")).Append("$ ").Append(reset).Append(Sgr("97m"))
          .Append("git commit -m \"P2-03: interim terminal engine\"").Append(reset);

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(15);
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
