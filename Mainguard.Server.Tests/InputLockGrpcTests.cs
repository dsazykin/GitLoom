using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// P2-14 test 5 (TI-P2-14.6) — terminal input locking is enforced at the gRPC layer, not UI-only. A
/// <b>hand-crafted raw client</b> (the proto stub, not <c>DaemonClient</c>) that sends an input frame to a
/// locked agent is rejected server-side; the output (read) stream still works.
/// </summary>
public class InputLockGrpcTests
{
    [Fact]
    public async Task InputLock_GrpcLayer_LockedAgent_RejectsInput_ButReadStreamWorks()
    {
        using var fixture = new DaemonFixture();
        // Managed worker: its terminal is locked daemon-side.
        fixture.Services.GetRequiredService<TerminalLockRegistry>().Lock("locked-agent");

        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());

        // Select the locked agent, then read the banner — the read (output) stream is open.
        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "locked-agent" });
        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Contains("read-only", call.ResponseStream.Current.Raw.ToStringUtf8());

        // Sending input (a data frame) is rejected server-side.
        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFromUtf8("rm -rf /\n") });
        await call.RequestStream.CompleteAsync();

        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None)) { }
        });
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    [Fact]
    public async Task InputLock_GrpcLayer_UnlockedAgent_AcceptsInput_AndEchoes()
    {
        using var fixture = new DaemonFixture();
        // No lock registered for this agent — manual-mode agent, input flows (interim echo path).
        var client = new TerminalService.TerminalServiceClient(fixture.CreateChannel());
        using var call = client.Attach(fixture.AuthHeaders());

        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = "free-agent" });
        await call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFromUtf8("echo hi\n") });

        Assert.True(await call.ResponseStream.MoveNext(CancellationToken.None));
        Assert.Equal("echo hi\n", call.ResponseStream.Current.Raw.ToStringUtf8());

        await call.RequestStream.CompleteAsync();
    }
}
