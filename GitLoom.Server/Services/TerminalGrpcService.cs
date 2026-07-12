using System.Threading.Tasks;
using GitLoom.Protos.V1;
using Grpc.Core;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="TerminalService"/>. In the P2-02 skeleton the attach
/// stream echoes input <c>data</c> frames back as <c>raw</c> output frames — the P2-03
/// PTY engine replaces the echo behind the same bidi contract. Output frames are
/// <c>oneof { raw | grid }</c> from day one so P2-18 is not a proto break.
///
/// Transport only: the (later) PTY plumbing lives in Core/daemon services.
/// </summary>
public sealed class TerminalGrpcService : TerminalService.TerminalServiceBase
{
    public override async Task Attach(
        IAsyncStreamReader<TerminalInput> requestStream,
        IServerStreamWriter<TerminalOutput> responseStream,
        ServerCallContext context)
    {
        try
        {
            await foreach (var input in requestStream.ReadAllAsync(context.CancellationToken))
            {
                // The first frame carries the agent_id selector; skip it (no PTY yet).
                if (input.InputCase != TerminalInput.InputOneofCase.Data)
                {
                    continue;
                }

                await responseStream.WriteAsync(new TerminalOutput { Raw = input.Data });
            }
        }
        catch (System.OperationCanceledException)
        {
            // Client detached — normal stream teardown.
        }
    }
}
