using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;

namespace Mainguard.Tests.Terminal;

/// <summary>
/// Dev tool (not a comparison path): runs a command in a real PTY via the P2-03
/// <see cref="PtyProcessShim"/> and captures its raw output byte stream to a <c>.bytes</c> fixture.
///
/// <para><b>No timestamps are captured</b> — the fixture is bytes only, exactly what a conformant
/// replay consumes. Pacing/quiescence and the overall duration cap are driven purely by
/// <see cref="CancellationToken"/> timeouts (a timer) — there are no blocking waits anywhere here,
/// so this file stays clean under the P2-04 reviewer grep. Recording is a one-time step: the
/// committed <c>.bytes</c> is what CI replays, which makes replay deterministic regardless of the
/// (non-persistent) container apt state at record time.</para>
/// </summary>
public static class TranscriptRecorder
{
    /// <summary>
    /// Spawns <paramref name="command"/> under a PTY sized <paramref name="cols"/>×<paramref name="rows"/>
    /// with a fixed TERM, records raw output until the child exits, <paramref name="maxDuration"/>
    /// elapses, or <paramref name="maxBytes"/> is reached. When <paramref name="inputScript"/> is
    /// supplied, the next chunk is written each time output goes quiet for <paramref name="quietWindow"/>
    /// (simple interactive scripting for vim/tmux — send key, wait for the app to settle, send next).
    /// </summary>
    public static byte[] Record(
        string command,
        IReadOnlyList<string> args,
        int cols,
        int rows,
        TimeSpan maxDuration,
        int maxBytes = 512 * 1024,
        IReadOnlyList<byte[]>? inputScript = null,
        TimeSpan? quietWindow = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TERM"] = "xterm-256color",
            ["LANG"] = "C.UTF-8",
            ["LC_ALL"] = "C.UTF-8",
            ["COLUMNS"] = cols.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["LINES"] = rows.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        using var session = PtyProcessShim.Spawn(command, args, Directory.GetCurrentDirectory(), env, cols, rows);
        return CaptureAsync(session, maxDuration, maxBytes, inputScript, quietWindow ?? TimeSpan.FromMilliseconds(500))
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    private static async Task<byte[]> CaptureAsync(
        PtySession session,
        TimeSpan maxDuration,
        int maxBytes,
        IReadOnlyList<byte[]>? inputScript,
        TimeSpan quietWindow)
    {
        var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var scriptIndex = 0;

        using var overall = new CancellationTokenSource(maxDuration);
        while (!overall.IsCancellationRequested && output.Length < maxBytes)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            readCts.CancelAfter(quietWindow); // timer, not a sleep

            int n;
            try
            {
                n = await session.IO.ReadAsync(buffer.AsMemory(), readCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (overall.IsCancellationRequested)
                {
                    break;
                }

                // Output went quiet: advance the interactive script, or finish if it's exhausted.
                if (inputScript is not null)
                {
                    if (scriptIndex >= inputScript.Count)
                    {
                        break;
                    }

                    await session.IO.WriteAsync(inputScript[scriptIndex++].AsMemory(), overall.Token)
                        .ConfigureAwait(false);
                }

                continue;
            }
            catch (IOException)
            {
                break; // PTY closed as the child exited
            }

            if (n == 0)
            {
                break; // EOF — child exited
            }

            output.Write(buffer, 0, n);
        }

        session.Kill();
        return output.ToArray();
    }
}
