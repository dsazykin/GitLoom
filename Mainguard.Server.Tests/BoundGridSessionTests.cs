using System.IO.Pipelines;
using System.Text;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Terminal.Vterm;
using Mainguard.Agents.UI.Controls;
using Mainguard.Protos.V1;
using Mainguard.Server.Runtime;
using Mainguard.Server.Terminal;

namespace Mainguard.Server.Tests;

/// <summary>
/// The P2-18 grid path through the REAL <see cref="BoundTerminalSession"/> pump: a scripted CLI
/// behind the <see cref="ITerminalSession"/> seam feeds the 16 ms streamer, the session's vterm
/// engine coalesces damage, and a grid subscriber receives an atomic snapshot + live deltas that
/// mirror the CLI's screen. Also covers the daemon-side OSC 52 clipboard frames (queries dropped),
/// the raw-subscriber + <see cref="BoundTerminalSession.TailText"/> paths staying intact alongside
/// the engine, resize (PTY + vterm in the same breath → snapshot), and the scrollback fetch.
/// Skipped where libvterm is absent (CI requires it).
/// </summary>
public sealed class BoundGridSessionTests
{
    private static readonly TerminalEngineConfig Libvterm = new(TerminalEngineKind.Libvterm);

    private static bool Available => VtermSession.IsSupported;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task GridAttach_SnapshotThenDeltas_MirrorTheCliScreen()
    {
        if (!Available)
        {
            return;
        }

        using var cli = new FakeTerminalSession();
        using var bound = new BoundTerminalSession("agent-grid", cli, Libvterm, cols: 40, rows: 6);

        var (snapshot, live) = bound.SubscribeGrid(out var unsubscribe);
        try
        {
            var client = new GridModel();
            client.ApplyGrid(snapshot);
            Assert.Equal(40, client.Cols);
            Assert.Equal(6, client.Rows);

            await cli.EmitAsync("hello \u001b[1;31mgrid\u001b[0m world");
            await ApplyUntilAsync(client, live, () => client.RowText(0) == "hello grid world");
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public async Task Osc52_EmitsClipboardFrames_QueriesNever()
    {
        if (!Available)
        {
            return;
        }

        using var cli = new FakeTerminalSession();
        using var bound = new BoundTerminalSession("agent-clip", cli, Libvterm, 40, 6);

        var (_, live) = bound.SubscribeGrid(out var unsubscribe);
        try
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("copied-in-jail"));
            await cli.EmitAsync($"\u001b]52;c;?\u001b]52;c;{payload}");

            string? copied = null;
            using var cts = new CancellationTokenSource(Timeout);
            await foreach (var frame in live.ReadAllAsync(cts.Token))
            {
                if (frame.FrameCase == TerminalOutput.FrameOneofCase.Clipboard)
                {
                    copied = frame.Clipboard.Text;
                    break;
                }
            }

            // The SET arrived; had the query been answered first, a second clipboard frame (or a
            // response write toward the PTY) would exist — WrittenInput stays empty below.
            Assert.Equal("copied-in-jail", copied);
            Assert.Equal(0, cli.WrittenInputBytes);
        }
        finally
        {
            unsubscribe();
        }
    }

    // MG-5: OSC 52 copy-out is an OUTPUT-pipeline event, so it is NOT gated by the terminal input-lock
    // and previously fired the operator's host clipboard even on a locked, view-only session. On a locked
    // session the copy-out must be dropped (output must not become a covert write channel to the host).
    // This mirrors Osc52_EmitsClipboardFrames_QueriesNever (which proves the unlocked path DELIVERS the
    // frame) — here the identical input yields NO clipboard frame, so the read window times out.
    [Fact]
    public async Task Osc52_OnInputLockedSession_CopyOutIsSuppressed()
    {
        if (!Available)
        {
            return;
        }

        using var cli = new FakeTerminalSession();
        using var bound = new BoundTerminalSession("agent-locked", cli, Libvterm, 40, 6, isInputLocked: () => true);

        var (_, live) = bound.SubscribeGrid(out var unsubscribe);
        try
        {
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("copied-in-jail"));
            await cli.EmitAsync($"\u001b]52;c;?\u001b]52;c;{payload}");

            string? copied = null;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await foreach (var frame in live.ReadAllAsync(cts.Token))
                {
                    if (frame.FrameCase == TerminalOutput.FrameOneofCase.Clipboard)
                    {
                        copied = frame.Clipboard.Text;
                        break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected: the copy-out was dropped, so no clipboard frame ever arrives.
            }

            Assert.Null(copied);
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public async Task RawSubscribers_AndTailText_KeepWorking_WithTheEngineOn()
    {
        if (!Available)
        {
            return;
        }

        using var cli = new FakeTerminalSession();
        using var bound = new BoundTerminalSession("agent-raw", cli, Libvterm, 40, 6);

        var (replay, live) = bound.Subscribe(out var unsubscribe);
        try
        {
            // The Ink cursor-column layout: words separated by ESC[nG, not spaces — TailText's
            // wiring contract (escapes become spaces, never nothing).
            await cli.EmitAsync("Failed\u001b[9Gto\u001b[13Gconnect");

            var received = new List<byte>(replay.SelectMany(f => f));
            using var cts = new CancellationTokenSource(Timeout);
            while (!Encoding.UTF8.GetString(received.ToArray()).Contains("connect"))
            {
                Assert.True(await live.WaitToReadAsync(cts.Token));
                while (live.TryRead(out var frame))
                {
                    received.AddRange(frame);
                }
            }

            Assert.Equal("Failed to connect", bound.TailText(100));
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public async Task Resize_PropagatesToPtyAndVterm_AndSnapshotsTheNewGeometry()
    {
        if (!Available)
        {
            return;
        }

        using var cli = new FakeTerminalSession();
        using var bound = new BoundTerminalSession("agent-resize", cli, Libvterm, 40, 6);

        var (snapshot, live) = bound.SubscribeGrid(out var unsubscribe);
        try
        {
            var client = new GridModel();
            client.ApplyGrid(snapshot);

            await cli.EmitAsync("before-resize");
            await ApplyUntilAsync(client, live, () => client.RowText(0) == "before-resize");

            bound.Resize(50, 10);
            Assert.Equal((50, 10), cli.LastResize); // the PTY half of one-authoritative-size
            await ApplyUntilAsync(client, live, () => client is { Cols: 50, Rows: 10 });
            Assert.Equal("before-resize", client.RowText(0)); // reflow kept the content
        }
        finally
        {
            unsubscribe();
        }
    }

    [Fact]
    public async Task GetScrollback_ServesThePushedRows()
    {
        if (!Available)
        {
            return;
        }

        using var cli = new FakeTerminalSession();
        using var bound = new BoundTerminalSession("agent-sb", cli, Libvterm, 40, 3);

        var (snapshot, live) = bound.SubscribeGrid(out var unsubscribe);
        try
        {
            var client = new GridModel();
            client.ApplyGrid(snapshot);

            await cli.EmitAsync(string.Join("\r\n", Enumerable.Range(0, 8).Select(i => $"sb-{i}")));
            await ApplyUntilAsync(client, live, () => client.RowText(2) == "sb-7");

            var reply = bound.GetScrollback(0, 100);
            Assert.True((int)reply.Total >= 5);
            Assert.Equal(0u, reply.Start);
            Assert.Contains(reply.Rows, r => RowText(r) == "sb-0");

            // The client ring mirrored the same pushes.
            Assert.Equal((int)reply.Total, client.ScrollbackCount);
        }
        finally
        {
            unsubscribe();
        }
    }

    // ---- helpers ----

    private static async Task ApplyUntilAsync(
        GridModel client,
        System.Threading.Channels.ChannelReader<TerminalOutput> live,
        Func<bool> done)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!done())
        {
            Assert.True(await live.WaitToReadAsync(cts.Token), "grid stream completed before the condition held");
            while (live.TryRead(out var frame))
            {
                client.Apply(frame);
            }
        }
    }

    private static string RowText(GridRow row)
    {
        var sb = new StringBuilder();
        foreach (var run in row.Runs)
        {
            for (var i = 0; i < run.Blanks; i++)
            {
                sb.Append(' ');
            }

            sb.Append(run.Packed);
            foreach (var glyph in run.Glyphs)
            {
                sb.Append(glyph);
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>A scripted CLI behind the <see cref="ITerminalSession"/> seam (duplex pipes).</summary>
    private sealed class FakeTerminalSession : ITerminalSession
    {
        private readonly Pipe _output = new(); // CLI → daemon
        private readonly Pipe _input = new();  // daemon → CLI
        private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _writtenInput;

        public FakeTerminalSession()
        {
            IO = new DuplexStream(this, _output.Reader.AsStream(), _input.Writer.AsStream());
        }

        public Stream IO { get; }

        public Task<int> ExitCode => _exit.Task;

        public (int Cols, int Rows)? LastResize { get; private set; }

        public long WrittenInputBytes => Interlocked.Read(ref _writtenInput);

        public void Resize(int cols, int rows) => LastResize = (cols, rows);

        public void Kill()
        {
            _exit.TrySetResult(137);
            _output.Writer.Complete();
        }

        public void Dispose() => Kill();

        public async Task EmitAsync(string text)
        {
            await _output.Writer.WriteAsync(Encoding.UTF8.GetBytes(text));
            await _output.Writer.FlushAsync();
        }

        private void CountInput(int bytes) => Interlocked.Add(ref _writtenInput, bytes);

        private sealed class DuplexStream : Stream
        {
            private readonly FakeTerminalSession _owner;
            private readonly Stream _read;
            private readonly Stream _write;

            public DuplexStream(FakeTerminalSession owner, Stream read, Stream write)
            {
                _owner = owner;
                _read = read;
                _write = write;
            }

            public override bool CanRead => true;

            public override bool CanWrite => true;

            public override bool CanSeek => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
                => _read.ReadAsync(buffer, cancellationToken);

            public override void Write(byte[] buffer, int offset, int count)
            {
                _owner.CountInput(count);
                _write.Write(buffer, offset, count);
            }

            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                _owner.CountInput(buffer.Length);
                return _write.WriteAsync(buffer, cancellationToken);
            }

            public override void Flush() => _write.Flush();

            public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
