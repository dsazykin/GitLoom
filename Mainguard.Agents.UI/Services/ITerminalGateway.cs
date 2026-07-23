using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Protos.V1;

namespace Mainguard.Agents.UI.Services;

/// <summary>
/// The ViewModel-facing seam onto the daemon terminal stream. Keeping the gRPC bidi call behind an
/// interface lets the <see cref="ViewModels.TerminalViewModel"/> be tested with a fake (no daemon),
/// and keeps the App's only daemon touch-point the <see cref="DaemonClient"/> (G-18).
/// </summary>
public interface ITerminalGateway : IDisposable
{
    /// <summary>Raised for each <c>raw</c> output frame the daemon streams from the PTY.</summary>
    event Action<ReadOnlyMemory<byte>>? OutputReceived;

    /// <summary>Attaches to <paramref name="agentId"/> and begins pumping output until cancelled.</summary>
    Task AttachAsync(string agentId, CancellationToken ct);

    /// <summary>Sends keystrokes/paste toward the PTY.</summary>
    Task SendInputAsync(ReadOnlyMemory<byte> data);

    /// <summary>Sends a terminal resize (SIGWINCH) toward the PTY.</summary>
    Task SendResizeAsync(int cols, int rows);
}

/// <summary>
/// <see cref="ITerminalGateway"/> over the daemon's <c>TerminalService.Attach</c> bidi stream via
/// <see cref="DaemonClient"/>. Writes the first selector frame, then forwards input/resize frames
/// and raises <see cref="OutputReceived"/> for each output frame.
///
/// <para>P2-18: with the grid engine selected, the first frame is <c>AttachOptions(grid: true)</c>
/// and grid/clipboard frames are forwarded as serialized <see cref="TerminalOutput"/> envelopes
/// through the SAME byte event — the ViewModel shuttles opaque bytes either way (zero VM change),
/// and the engine control on the other end knows which encoding it subscribed for. <c>raw</c>
/// frames keep their P2-03 byte semantics untouched.</para>
/// </summary>
public sealed class DaemonTerminalGateway : ITerminalGateway
{
    private readonly DaemonClient _client;
    private readonly bool _grid;
    private Grpc.Core.AsyncDuplexStreamingCall<TerminalInput, TerminalOutput>? _call;
    private CancellationTokenSource? _cts;

    public DaemonTerminalGateway(DaemonClient client, bool? useGridEngine = null)
    {
        _client = client;
        _grid = useGridEngine ?? TerminalEngineSelection.UseGridEngine;
    }

    public event Action<ReadOnlyMemory<byte>>? OutputReceived;

    public async Task AttachAsync(string agentId, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _call = _client.AttachTerminal(_cts.Token);
        var first = _grid
            ? new TerminalInput { Attach = new AttachOptions { AgentId = agentId, Grid = true } }
            : new TerminalInput { AgentId = agentId };
        await _call.RequestStream.WriteAsync(first);

        try
        {
            await foreach (var output in _call.ResponseStream.ReadAllAsync(_cts.Token))
            {
                switch (output.FrameCase)
                {
                    case TerminalOutput.FrameOneofCase.Raw:
                        OutputReceived?.Invoke(output.Raw.Memory);
                        break;
                    case TerminalOutput.FrameOneofCase.Grid:
                    case TerminalOutput.FrameOneofCase.Clipboard:
                        OutputReceived?.Invoke(output.ToByteArray());
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Detached — normal teardown.
        }
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> data)
    {
        if (_call is null)
        {
            return;
        }

        await _call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFrom(data.Span) });
    }

    public async Task SendResizeAsync(int cols, int rows)
    {
        if (_call is null)
        {
            return;
        }

        await _call.RequestStream.WriteAsync(new TerminalInput
        {
            Resize = new Resize { Cols = (uint)cols, Rows = (uint)rows },
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _call?.Dispose();
        _cts?.Dispose();
    }
}
