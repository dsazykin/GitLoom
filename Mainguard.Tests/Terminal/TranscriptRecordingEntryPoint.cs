using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// Dev-only entry point that (re)captures the real-program transcripts through a PTY. It is gated on
/// <c>MAINGUARD_RECORD_TRANSCRIPTS=1</c> and skips otherwise, so it never runs in CI or a normal
/// <c>dotnet test</c>. Run it once inside the Linux container after apt-installing the programs:
///
/// <code>
///   docker compose run --rm shell
///   apt-get update &amp;&amp; apt-get install -y vim htop tmux
///   MAINGUARD_RECORD_TRANSCRIPTS=1 dotnet test Mainguard.slnx \
///     --filter FullyQualifiedName~RecordRealTranscripts
///   # then regenerate goldens for the new bytes:
///   MAINGUARD_REGEN_GOLDENS=1 dotnet test Mainguard.slnx --filter FullyQualifiedName~TranscriptReplay
/// </code>
///
/// The committed <c>.bytes</c> are what CI replays; recording is one-time. <c>claude-code</c> and
/// <c>opencode</c> are proprietary and cannot be recorded here — their committed streams are
/// synthetic (see <c>Transcripts/README.md</c>) and are not produced by this entry point.
/// </summary>
public sealed class TranscriptRecordingEntryPoint
{
    [Fact]
    public void RecordRealTranscripts()
    {
        if (!TerminalHarnessPaths.RecordTranscripts)
        {
            return; // no-op unless MAINGUARD_RECORD_TRANSCRIPTS=1 (dev capture only)
        }

        Directory.CreateDirectory(TerminalHarnessPaths.TranscriptsDir);
        byte[] Esc(string s) => Encoding.ASCII.GetBytes(s);

        // vim: open, enter insert mode, type a line, escape, save+quit.
        Save("vim", TranscriptRecorder.Record(
            "vim", new[] { "-u", "NONE", "-N", "/tmp/gitloom-rec.txt" }, 80, 24,
            maxDuration: TimeSpan.FromSeconds(20),
            inputScript: new List<byte[]>
            {
                Esc("i"), Esc("hello, world"), Esc(""), Esc(":wq\r"),
            }));

        // htop: run for 60 s of live refreshes, then quit.
        Save("htop-60s", TranscriptRecorder.Record(
            "htop", new[] { "-d", "10" }, 80, 24,
            maxDuration: TimeSpan.FromSeconds(60),
            inputScript: new List<byte[]> { Esc("q") },
            quietWindow: TimeSpan.FromSeconds(2)));

        // tmux: start a session, split, switch pane, exit.
        Save("tmux", TranscriptRecorder.Record(
            "tmux", new[] { "new-session", "-x", "80", "-y", "24" }, 80, 24,
            maxDuration: TimeSpan.FromSeconds(20),
            inputScript: new List<byte[]>
            {
                Esc("%"), Esc("o"), Esc("exit\r"), Esc("exit\r"),
            }));
    }

    private static void Save(string name, byte[] bytes)
        => File.WriteAllBytes(Path.Combine(TerminalHarnessPaths.TranscriptsDir, name + ".bytes"), bytes);
}
