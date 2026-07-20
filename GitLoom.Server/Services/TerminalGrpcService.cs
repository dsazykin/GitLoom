using System;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using GitLoom.Protos.V1;
using GitLoom.Server.Auth;
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
    private readonly TerminalLockRegistry _locks;

    public TerminalGrpcService(TerminalSessionManager sessions, TerminalLockRegistry locks)
    {
        _sessions = sessions;
        _locks = locks;
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

            // P2-14 terminal input lock: a managed worker's terminal is read-only. The read (output)
            // stream stays open — a banner + the live output prove it — but input DATA frames are
            // refused server-side (the RoleInterceptor also severs them at the gRPC layer; this is
            // defense-in-depth so a direct service call is enforced too). Never UI-only.
            var locked = agentId is not null && _locks is not null && _locks.IsLocked(agentId);

            // The real agent path: a long-lived CLI session bound at spawn (P2-47 #3 wiring). Attach
            // subscribes (replay + live frames); detach only unsubscribes — the CLI keeps running.
            var bound = agentId is not null ? _sessions.TryGetBound(agentId) : null;
            if (bound is not null)
            {
                await PumpBoundAsync(bound, requestStream, responseStream, locked, ct);
                return;
            }

            if (locked)
            {
                await LockedAttachAsync(requestStream, responseStream, first, ct);
                return;
            }

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

    /// <summary>
    /// The bound-session attach: replay the missed tail, then pump live frames, while forwarding
    /// input/resize toward the CLI. A locked (managed-worker) attach keeps the read stream open but
    /// refuses input DATA frames with <see cref="StatusCode.PermissionDenied"/>. The client's
    /// detach unsubscribes only — the CLI's PTY belongs to the agent lifecycle, not the attach.
    /// </summary>
    private static async Task PumpBoundAsync(
        Runtime.BoundTerminalSession bound,
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        bool locked,
        System.Threading.CancellationToken ct)
    {
        var (replay, live) = bound.Subscribe(out var unsubscribe);
        try
        {
            if (locked)
            {
                await responseStream.WriteAsync(new TerminalOutput
                {
                    Raw = ByteString.CopyFromUtf8("[read-only - managed worker]\r\n"),
                });
            }

            // Single writer to the response stream: this pump task emits replay-then-live frames.
            var pump = Task.Run(async () =>
            {
                foreach (var frame in replay)
                {
                    await responseStream.WriteAsync(new TerminalOutput { Raw = ByteString.CopyFrom(frame) });
                }

                await foreach (var frame in live.ReadAllAsync(ct))
                {
                    await responseStream.WriteAsync(new TerminalOutput { Raw = ByteString.CopyFrom(frame) });
                }
            }, ct);

            try
            {
                await foreach (var input in requestStream.ReadAllAsync(ct))
                {
                    switch (input.InputCase)
                    {
                        case TerminalInput.InputOneofCase.Data:
                            if (locked)
                            {
                                throw new RpcException(new Status(StatusCode.PermissionDenied,
                                    "This terminal is locked (managed worker) — input is denied. The read stream stays open."));
                            }

                            await bound.WriteInputAsync(input.Data.Memory, ct);
                            break;
                        case TerminalInput.InputOneofCase.Resize:
                            bound.Resize((int)input.Resize.Cols, (int)input.Resize.Rows);
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Client detached mid-stream — normal teardown; the session lives on.
            }

            // Keep streaming output until the client cancels or the CLI's session ends (a client
            // that completed its request stream is a legitimate read-only viewer). Never kill the
            // CLI from an attach teardown.
            try
            {
                await pump;
            }
            catch (OperationCanceledException)
            {
                // Detach — normal.
            }
            catch (RpcException)
            {
                // The client went away mid-write — nothing to salvage; the session lives on.
            }
        }
        finally
        {
            unsubscribe();
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
    /// The P2-14 locked (read-only) attach path for a managed worker. Writes a read-only banner so the
    /// output (read) stream is demonstrably open, then reads input and rejects any <c>data</c> frame with
    /// <see cref="StatusCode.PermissionDenied"/> — the input stream is severed daemon-side, never UI-only.
    /// </summary>
    private static async Task LockedAttachAsync(
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        TerminalInput first,
        System.Threading.CancellationToken ct)
    {
        // Read direction works: a banner is delivered on attach.
        await responseStream.WriteAsync(new TerminalOutput
        {
            Raw = ByteString.CopyFromUtf8("[read-only - managed worker]\r\n"),
        });

        // The first frame was the agent_id selector; if it somehow carried data, reject it too.
        if (first.InputCase == TerminalInput.InputOneofCase.Data)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied,
                "This terminal is locked (managed worker) — input is denied."));
        }

        await foreach (var input in requestStream.ReadAllAsync(ct))
        {
            if (input.InputCase == TerminalInput.InputOneofCase.Data)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied,
                    "This terminal is locked (managed worker) — input is denied. The read stream stays open."));
            }

            // Resize / stray agent_id frames are harmless and ignored.
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
