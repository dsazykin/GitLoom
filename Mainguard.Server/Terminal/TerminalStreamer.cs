using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Terminal;

namespace Mainguard.Server.Terminal;

/// <summary>
/// Batches PTY output into gRPC <c>raw</c> frames on a fixed cadence without ever splitting a VT
/// escape sequence or a UTF-8 codepoint across a frame boundary.
///
/// <para>Bytes read off the PTY are accumulated in a pooled carry buffer via <see cref="Ingest"/>.
/// Every flush tick, <see cref="TryDrain"/> runs <see cref="VtBoundaryDetector"/> over the carry
/// and emits the largest safe prefix as one frame, retaining the incomplete tail as the next carry.
/// A malformed endless escape can never buffer unboundedly: once the carry reaches
/// <see cref="HoldbackCap"/> with no boundary, the whole carry is flushed regardless (edge row 2).
/// Pooled buffers are returned on every path, fault included, so memory stays flat under a firehose
/// (edge row 3).</para>
///
/// <para><see cref="Ingest"/>/<see cref="TryDrain"/> are the deterministic, timing-free core (unit
/// tested directly); <see cref="RunAsync"/> is the production pump that wires a PTY stream and a
/// 16 ms ticker around them.</para>
/// </summary>
public sealed class TerminalStreamer : IDisposable
{
    /// <summary>Carry cap: at this size with no VT boundary in sight, flush anyway.</summary>
    public const int HoldbackCap = 4096;

    /// <summary>The 16 ms flush cadence (~60 fps) the master doc specifies.</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(16);

    private const int ReadChunk = 64 * 1024;

    private readonly VtBoundaryDetector _detector = new();
    private readonly int _holdbackCap;
    private readonly object _gate = new();

    private byte[] _carry = Array.Empty<byte>();
    private int _carryLen;
    private long _pooledHighWaterBytes;
    private bool _disposed;

    public TerminalStreamer(int holdbackCap = HoldbackCap)
    {
        if (holdbackCap <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(holdbackCap));
        }

        _holdbackCap = holdbackCap;
    }

    /// <summary>Bytes currently held back (the incomplete tail). Test seam for the flat-memory assertion.</summary>
    internal int PendingBytes
    {
        get
        {
            lock (_gate)
            {
                return _carryLen;
            }
        }
    }

    /// <summary>High-water mark of the pooled carry capacity. Test seam for the flat-memory assertion.</summary>
    internal long PooledHighWaterBytes
    {
        get
        {
            lock (_gate)
            {
                return _pooledHighWaterBytes;
            }
        }
    }

    /// <summary>Appends freshly-read PTY bytes to the carry (thread-safe).</summary>
    public void Ingest(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        lock (_gate)
        {
            EnsureCapacity(_carryLen + data.Length);
            data.CopyTo(_carry.AsSpan(_carryLen));
            _carryLen += data.Length;
        }
    }

    /// <summary>
    /// Emits the largest currently-safe prefix as one frame, retaining the incomplete tail. Returns
    /// <c>false</c> when nothing is safe yet and the holdback cap has not been reached.
    /// </summary>
    public bool TryDrain(out byte[] frame)
    {
        lock (_gate)
        {
            if (_carryLen == 0)
            {
                frame = Array.Empty<byte>();
                return false;
            }

            var safeLen = _detector.SafeFlushLength(_carry.AsSpan(0, _carryLen));

            if (safeLen == 0)
            {
                if (_carryLen < _holdbackCap)
                {
                    frame = Array.Empty<byte>();
                    return false; // hold and wait for the sequence to complete
                }

                // Holdback cap reached with no boundary: flush everything regardless (edge row 2).
                safeLen = _carryLen;
            }

            frame = _carry.AsSpan(0, safeLen).ToArray();

            var tail = _carryLen - safeLen;
            if (tail > 0)
            {
                _carry.AsSpan(safeLen, tail).CopyTo(_carry.AsSpan(0));
            }

            _carryLen = tail;
            return true;
        }
    }

    /// <summary>
    /// Production pump: reads <paramref name="source"/> (a <see cref="Mainguard.Agents.Agents.PtySession.IO"/>
    /// stream) into pooled buffers, and on a fixed <paramref name="flushInterval"/> drains one safe
    /// frame to <paramref name="emitFrameAsync"/>. Completes when the stream ends or the token trips.
    /// </summary>
    public async Task RunAsync(
        Stream source,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> emitFrameAsync,
        TimeSpan? flushInterval = null,
        CancellationToken ct = default)
    {
        var interval = flushInterval ?? DefaultFlushInterval;
        using var timer = new PeriodicTimer(interval);
        using var readDone = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, readDone.Token);

        var readLoop = Task.Run(() => ReadLoopAsync(source, readDone, ct), CancellationToken.None);

        try
        {
            while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
            {
                await FlushOnceAsync(emitFrameAsync, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Read loop finished (EOF) or the caller cancelled — fall through to a final flush.
        }

        await readLoop.ConfigureAwait(false);

        // Final drain so the last safe bytes are never stranded in the carry.
        await FlushOnceAsync(emitFrameAsync, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(Stream source, CancellationTokenSource readDone, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadChunk);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, ReadChunk), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    break; // PTY closed underneath us
                }

                if (read <= 0)
                {
                    break; // EOF: child exited / PTY closed
                }

                Ingest(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            readDone.Cancel();
        }
    }

    private async Task FlushOnceAsync(Func<ReadOnlyMemory<byte>, CancellationToken, Task> emitFrameAsync, CancellationToken ct)
    {
        while (TryDrain(out var frame))
        {
            await emitFrameAsync(frame, ct).ConfigureAwait(false);
        }
    }

    private void EnsureCapacity(int required)
    {
        if (_carry.Length >= required)
        {
            return;
        }

        var next = ArrayPool<byte>.Shared.Rent(Math.Max(required, ReadChunk));
        if (_carryLen > 0)
        {
            Array.Copy(_carry, next, _carryLen);
        }

        if (_carry.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(_carry);
        }

        _carry = next;
        if (_carry.Length > _pooledHighWaterBytes)
        {
            _pooledHighWaterBytes = _carry.Length;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_carry.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_carry);
                _carry = Array.Empty<byte>();
                _carryLen = 0;
            }
        }
    }
}
