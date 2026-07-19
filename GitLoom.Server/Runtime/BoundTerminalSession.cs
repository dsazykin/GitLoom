using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using GitLoom.Server.Terminal;

namespace GitLoom.Server.Runtime;

/// <summary>
/// A long-lived, agent-bound terminal session: the CLI's PTY outlives any single gRPC attach. One
/// continuous <see cref="TerminalStreamer"/> pump drains the PTY into VT-safe frames that are
/// (a) kept in a bounded replay ring — so a re-attach after the client detached/switched agents
/// renders the missed output composed ("Reattached — session continued", ControlCenterDesign §4.5) —
/// and (b) fanned out to every live subscriber. Detaching a client only drops its subscription;
/// killing the CLI is an explicit <see cref="Kill"/> (StopAgent / daemon teardown), never a side
/// effect of closing a terminal document.
///
/// <para>The continuous pump also keeps the PTY drained while nobody watches, so a chatty CLI can
/// never block on a full PTY buffer between attaches.</para>
/// </summary>
public sealed class BoundTerminalSession : IDisposable
{
    /// <summary>Replay ring cap — enough to redraw a busy TUI, bounded so daemon memory stays flat.</summary>
    internal const int ReplayCapBytes = 512 * 1024;

    /// <summary>Per-subscriber frame buffer; a stalled attach is completed (it re-attaches + replays).</summary>
    internal const int SubscriberFrameCapacity = 1024;

    private readonly ITerminalSession _session;
    private readonly TerminalStreamer _streamer = new();
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly Task _pump;
    private readonly object _gate = new();
    private readonly LinkedList<byte[]> _replay = new();
    private readonly List<Channel<byte[]>> _subscribers = new();
    private int _replayBytes;
    private bool _completed;
    private int _disposed;

    public BoundTerminalSession(string agentId, ITerminalSession session)
    {
        AgentId = agentId ?? throw new ArgumentNullException(nameof(agentId));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _pump = Task.Run(PumpAsync);
    }

    public string AgentId { get; }

    /// <summary>Completes when the child exits (the binder marks the session state off this).</summary>
    public Task<int> ExitCode => _session.ExitCode;

    /// <summary>
    /// Opens one subscription: the replay tail as already-safe frames, plus a live reader for
    /// everything after it (atomic with the replay — no frame is lost or duplicated in between).
    /// Call <paramref name="unsubscribe"/> on detach; the session itself keeps running.
    /// </summary>
    public (IReadOnlyList<byte[]> Replay, ChannelReader<byte[]> Live) Subscribe(out Action unsubscribe)
    {
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SubscriberFrameCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait, // Wait + TryWrite == "report full", never block
        });

        byte[][] replay;
        lock (_gate)
        {
            replay = _replay.ToArray();
            if (_completed)
            {
                channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        unsubscribe = () =>
        {
            lock (_gate)
            {
                _subscribers.Remove(channel);
            }

            channel.Writer.TryComplete();
        };

        return (replay, channel.Reader);
    }

    /// <summary>Writes keystrokes/paste toward the CLI.</summary>
    public async Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await _session.IO.WriteAsync(data, ct).ConfigureAwait(false);
        await _session.IO.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Propagates a resize (SIGWINCH) toward the CLI. Invalid sizes are ignored.</summary>
    public void Resize(int cols, int rows)
    {
        if (cols > 0 && rows > 0)
        {
            _session.Resize(cols, rows);
        }
    }

    /// <summary>Force-terminates the CLI (StopAgent / teardown). Attaches see the stream complete.</summary>
    public void Kill() => _session.Kill();

    /// <summary>
    /// A human-readable tail of the CLI's most recent output (from the replay ring), for the
    /// death-diagnosis audit: VT escape sequences and control bytes are stripped, whitespace runs
    /// collapsed, and the result capped to the LAST <paramref name="maxChars"/> characters.
    /// </summary>
    public string TailText(int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        byte[][] frames;
        lock (_gate)
        {
            frames = _replay.ToArray();
        }

        var raw = System.Text.Encoding.UTF8.GetString(
            frames.SelectMany(f => f).ToArray());

        // Strip CSI/OSC escape sequences, then every remaining control char becomes whitespace.
        raw = System.Text.RegularExpressions.Regex.Replace(
            raw, @"\x1B(\[[0-9;?]*[ -/]*[@-~]|\][^\x07\x1B]*(\x07|\x1B\\)?|[@-Z\\-_])", string.Empty);
        var cleaned = System.Text.RegularExpressions.Regex.Replace(
            new string(raw.Select(c => char.IsControl(c) ? ' ' : c).ToArray()), @"\s+", " ").Trim();

        return cleaned.Length <= maxChars ? cleaned : cleaned[^maxChars..];
    }

    private async Task PumpAsync()
    {
        try
        {
            await _streamer.RunAsync(_session.IO, (frame, _) =>
            {
                Publish(frame.ToArray());
                return Task.CompletedTask;
            }, flushInterval: null, _pumpCts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // PTY torn down underneath the pump — fall through to completion.
        }
        finally
        {
            CompleteSubscribers();
        }
    }

    private void Publish(byte[] frame)
    {
        lock (_gate)
        {
            _replay.AddLast(frame);
            _replayBytes += frame.Length;
            while (_replayBytes > ReplayCapBytes && _replay.First is { } oldest)
            {
                _replayBytes -= oldest.Value.Length;
                _replay.RemoveFirst();
            }

            for (var i = _subscribers.Count - 1; i >= 0; i--)
            {
                if (!_subscribers[i].Writer.TryWrite(frame))
                {
                    // A stalled attach: complete it (the client re-attaches and replays) rather
                    // than buffering unboundedly or silently dropping frames mid-stream.
                    _subscribers[i].Writer.TryComplete();
                    _subscribers.RemoveAt(i);
                }
            }
        }
    }

    private void CompleteSubscribers()
    {
        lock (_gate)
        {
            _completed = true;
            foreach (var subscriber in _subscribers)
            {
                subscriber.Writer.TryComplete();
            }

            _subscribers.Clear();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _session.Kill();
        }
        catch
        {
            // Best-effort reap.
        }

        _pumpCts.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Pump teardown races with PTY disposal.
        }

        _session.Dispose();
        _streamer.Dispose();
        _pumpCts.Dispose();
    }
}
