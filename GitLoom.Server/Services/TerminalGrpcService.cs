using System;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using GitLoom.Protos.V1;
using GitLoom.Server.Runtime;
using GitLoom.Server.Terminal;
using Google.Protobuf;
using Grpc.Core;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="TerminalService"/>. The first input frame selects the agent; the
/// daemon then drives that agent's <see cref="PtySession"/> through a <see cref="TerminalStreamer"/>
/// — PTY output is batched into <c>raw</c> frames on the 16 ms cadence (never splitting a VT
/// sequence or UTF-8 codepoint), input <c>data</c> frames are written to the PTY, and <c>resize</c>
/// frames propagate as SIGWINCH.
///
/// <para>Until the P2-09 agent lifecycle binds real processes, <see cref="TerminalSessionManager"/>
/// has no PTY factory and <see cref="TerminalSessionManager.Create"/> returns <c>null</c>; the
/// attach then falls back to the P2-02 echo so the bidi contract still round-trips. Output frames
/// are <c>oneof { raw | grid }</c> from day one so P2-18 is not a proto break.</para>
///
/// <para>Transport only: session ownership + PTY plumbing live in Core/daemon services.</para>
/// </summary>
public sealed class TerminalGrpcService : TerminalService.TerminalServiceBase
{
    private readonly TerminalSessionManager _sessions;

    public TerminalGrpcService(TerminalSessionManager sessions)
    {
        _sessions = sessions;
    }

    public override async Task Attach(
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        ServerCallContext context)
    {
        var ct = context.CancellationToken;

        try
        {
            // The first frame carries the agent_id selector.
            if (!await requestStream.MoveNext(ct))
            {
                return;
            }

            var first = requestStream.Current;
            var agentId = first.InputCase == TerminalInput.InputOneofCase.AgentId
                ? first.AgentId
                : null;

            var session = agentId is not null ? _sessions.Create(agentId) : null;
            if (session is null)
            {
                // Interim: no PTY bound for this agent yet — echo so the attach still round-trips.
                await EchoAsync(requestStream, responseStream, first, ct);
                return;
            }

            await PumpPtyAsync(session, requestStream, responseStream, ct);
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal stream teardown.
        }
    }

    private static async Task PumpPtyAsync(
        PtySession session,
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        System.Threading.CancellationToken ct)
    {
        using (session)
        using (var streamer = new TerminalStreamer())
        {
            // Single writer to the response stream: only the streamer emits raw frames.
            var pump = streamer.RunAsync(
                session.IO,
                (frame, token) => responseStream.WriteAsync(
                    new TerminalOutput { Raw = ByteString.CopyFrom(frame.Span) }),
                flushInterval: null,
                ct);

            try
            {
                await foreach (var input in requestStream.ReadAllAsync(ct))
                {
                    switch (input.InputCase)
                    {
                        case TerminalInput.InputOneofCase.Data:
                            var bytes = input.Data.Memory;
                            await session.IO.WriteAsync(bytes, ct);
                            await session.IO.FlushAsync(ct);
                            break;
                        case TerminalInput.InputOneofCase.Resize:
                            session.Resize((int)input.Resize.Cols, (int)input.Resize.Rows);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client detached mid-stream.
            }

            // Client's request stream ended → tear the child down so the PTY reaches EOF and the
            // streamer's read loop completes; then await the final drain.
            session.Kill();
            await pump;
        }
    }

    /// <summary>
    /// The P2-02 interim echo path: reflects input <c>data</c> frames back as <c>raw</c> output
    /// frames. Used when no PTY is bound for the selected agent (until P2-09).
    /// </summary>
    private static async Task EchoAsync(
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        TerminalInput first,
        System.Threading.CancellationToken ct)
    {
        if (first.InputCase == TerminalInput.InputOneofCase.Data)
        {
            await responseStream.WriteAsync(new TerminalOutput { Raw = first.Data });
        }

        await foreach (var input in requestStream.ReadAllAsync(ct))
        {
            if (input.InputCase != TerminalInput.InputOneofCase.Data)
            {
                continue;
            }

            await responseStream.WriteAsync(new TerminalOutput { Raw = input.Data });
        }
    }
}
