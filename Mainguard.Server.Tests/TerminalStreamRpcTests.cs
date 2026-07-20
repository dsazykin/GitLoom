using System.Collections.Generic;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Tests.Fixtures;

namespace Mainguard.Server.Tests;

/// <summary>TI-P2-02 §3 / plan §6 row 3 — terminal bidi echo round-trips bytes.</summary>
public sealed class TerminalStreamRpcTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public TerminalStreamRpcTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task TerminalAttach_BidiEcho_ShouldRoundTripBytes()
    {
        var client = new TerminalService.TerminalServiceClient(_daemon.CreateChannel());
        using var call = client.Attach(_daemon.AuthHeaders());

        // First frame selects the agent; then two data frames.
        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "agent-1" });
        var chunk1 = new byte[] { 1, 2, 3, 4, 5 };
        var chunk2 = new byte[] { 200, 128, 0, 42 };
        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFrom(chunk1) });
        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFrom(chunk2) });
        await call.RequestStream.CompleteAsync();

        var received = new List<byte>();
        await foreach (var output in call.ResponseStream.ReadAllAsync())
        {
            // The output frame is the oneof { raw | grid } — always raw until P2-18.
            Assert.Equal(TerminalOutput.FrameOneofCase.Raw, output.FrameCase);
            received.AddRange(output.Raw.ToByteArray());
        }

        var expected = new List<byte>();
        expected.AddRange(chunk1);
        expected.AddRange(chunk2);
        Assert.Equal(expected, received);
    }
}
