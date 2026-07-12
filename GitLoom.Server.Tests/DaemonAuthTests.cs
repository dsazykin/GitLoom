using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Protos.V1;
using GitLoom.Server;
using GitLoom.Server.Tests.Fixtures;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;

namespace GitLoom.Server.Tests;

/// <summary>
/// TI-P2-02 §1/§2/§6/§10 + plan §6 rows 1,2,6,6(loopback),7,8. Auth coverage (every
/// method by reflection), loopback bind, unimplemented stubs, token-file permissions,
/// port-already-bound.
/// </summary>
public sealed class DaemonAuthTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public DaemonAuthTests(DaemonFixture daemon) => _daemon = daemon;

    // TI.1 — authenticated unary call succeeds.
    [Fact]
    public async Task AuthenticatedCall_ShouldSucceed()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var response = await client.ListAgentsAsync(new ListAgentsRequest(), _daemon.AuthHeaders());
        Assert.Empty(response.Agents);
    }

    // TI.1 — wrong token, unary → PermissionDenied.
    [Fact]
    public async Task WrongToken_ShouldReturnPermissionDenied()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.ListAgentsAsync(new ListAgentsRequest(), _daemon.WrongTokenHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    // TI.1 — missing token → PermissionDenied.
    [Fact]
    public async Task MissingToken_ShouldReturnPermissionDenied()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            client.ListAgentsAsync(new ListAgentsRequest(), new Metadata()).ResponseAsync);
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    // §6.2 — the denial applies to streaming calls too.
    [Fact]
    public async Task WrongToken_ShouldReturnPermissionDenied_OnStreamingCall()
    {
        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        using var call = client.StreamAgentEvents(new StreamAgentEventsRequest(), _daemon.WrongTokenHeaders());
        var ex = await Assert.ThrowsAsync<RpcException>(async () =>
        {
            await foreach (var _ in call.ResponseStream.ReadAllAsync())
            {
            }
        });
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
    }

    // TI.1 / §6.6 — AuthCoverage_EveryMethodByReflection. Every service/method pair
    // reflected from the proto descriptor set is denied with a wrong token — new RPCs are
    // covered automatically, and a "public method" allowlist appearing later fails here.
    public static IEnumerable<object[]> EveryMethod()
        => ProtoServices()
            .SelectMany(s => s.Methods.Select(m => new object[] { s.FullName, m.Name }));

    [Theory]
    [MemberData(nameof(EveryMethod))]
    public async Task AuthCoverage_EveryMethodByReflection_WrongToken_Denied(string serviceFullName, string methodName)
    {
        var descriptor = ProtoServices().Single(s => s.FullName == serviceFullName)
            .Methods.Single(m => m.Name == methodName);
        var invoker = _daemon.CreateChannel().CreateCallInvoker();

        var ex = await Record.ExceptionAsync(() => InvokeDeniedAsync(invoker, descriptor, _daemon.WrongTokenHeaders()));

        var rpc = Assert.IsType<RpcException>(ex);
        Assert.Equal(StatusCode.PermissionDenied, rpc.StatusCode);
    }

    // §6.6(loopback) / TI.2 — the real host binds loopback only (127.0.0.1), never AnyIP.
    [Fact]
    public async Task Daemon_ShouldBindLoopbackOnly()
    {
        var port = FreePort();
        await using var app = await DaemonHost.StartAsync(new DaemonOptions
        {
            Port = port,
            LocalDev = true,
            TokenPath = TempToken(),
        });

        Assert.NotEmpty(app.Urls);
        foreach (var address in app.Urls)
        {
            var uri = new Uri(address);
            Assert.True(IPAddress.TryParse(uri.Host, out var ip), $"host not an IP: {uri.Host}");
            Assert.True(IPAddress.IsLoopback(ip!), $"not loopback: {uri.Host}");
        }
    }

    // §6.7 / TI.10 — RepoSync is implemented (P2-06): it now validates rather than stubbing
    // Unimplemented (an empty ProvisionRepo request → InvalidArgument). Gateway is implemented (P2-08):
    // GetBudgets now returns a budget rather than the old Unimplemented stub.
    [Fact]
    public async Task RepoSync_ValidatesRequest_And_Gateway_IsImplemented()
    {
        var repoSync = new RepoSyncService.RepoSyncServiceClient(_daemon.CreateChannel());
        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            repoSync.ProvisionRepoAsync(new ProvisionRepoRequest(), _daemon.AuthHeaders()).ResponseAsync);
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);

        var gateway = new GatewayService.GatewayServiceClient(_daemon.CreateChannel());
        var budgets = await gateway.GetBudgetsAsync(new GetBudgetsRequest(), _daemon.AuthHeaders());
        Assert.NotNull(budgets.Budget); // P2-08: implemented, no longer an Unimplemented stub
    }

    // §6.8 / TI — token-file permissions. Linux: mode 0600 (no group/other). Windows: skip.
    [Fact]
    public void TokenFile_Permissions_ShouldBeUserOnly()
    {
        var tokenFile = GitLoom.Server.Auth.SessionTokenFile.Create(TempToken());
        Assert.True(System.IO.File.Exists(tokenFile.Path));

        if (!OperatingSystem.IsWindows())
        {
            var mode = System.IO.File.GetUnixFileMode(tokenFile.Path);
            const System.IO.UnixFileMode groupOther =
                System.IO.UnixFileMode.GroupRead | System.IO.UnixFileMode.GroupWrite | System.IO.UnixFileMode.GroupExecute
                | System.IO.UnixFileMode.OtherRead | System.IO.UnixFileMode.OtherWrite | System.IO.UnixFileMode.OtherExecute;
            Assert.Equal(default, mode & groupOther);
        }
    }

    // TI.7 / edge row 4 — token file deleted while running: existing channels keep working
    // (the interceptor holds the token in memory; the file is only read at bootstrap).
    [Fact]
    public async Task TokenFileDeletedWhileRunning_ShouldNotBreakExistingChannels()
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        var tokenPath = TempToken();
        var port = FreePort();
        await using var app = await DaemonHost.StartAsync(new DaemonOptions { Port = port, LocalDev = true, TokenPath = tokenPath });
        var token = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<GitLoom.Server.Auth.SessionTokenFile>(app.Services).Token;

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}");
        var client = new AgentService.AgentServiceClient(channel);
        var headers = new Metadata { { "authorization", $"bearer {token}" } };

        await client.ListAgentsAsync(new ListAgentsRequest(), headers, deadline: DateTime.UtcNow.AddSeconds(10));

        // Delete the on-disk token — the running daemon must keep honoring live channels.
        System.IO.File.Delete(tokenPath);
        Assert.False(System.IO.File.Exists(tokenPath));

        var afterDelete = await client.ListAgentsAsync(new ListAgentsRequest(), headers, deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.Empty(afterDelete.Agents);
    }

    // §6 edge row 3 / TI.6 — port already bound → typed startup failure naming the port.
    [Fact]
    public async Task PortAlreadyBound_ShouldFailTypedNamingPort()
    {
        var port = FreePort();
        var blocker = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port);
        blocker.Start();
        try
        {
            var ex = await Assert.ThrowsAsync<DaemonStartupException>(() =>
                DaemonHost.StartAsync(new DaemonOptions { Port = port, LocalDev = true, TokenPath = TempToken() }));
            Assert.Equal(port, ex.Port);
            Assert.Contains(port.ToString(), ex.Message);
        }
        finally
        {
            blocker.Stop();
        }
    }

    private static IReadOnlyList<ServiceDescriptor> ProtoServices() => new[]
    {
        AgentReflection.Descriptor.Services.Single(),
        TerminalReflection.Descriptor.Services.Single(),
        ReposyncReflection.Descriptor.Services.Single(),
        GatewayReflection.Descriptor.Services.Single(),
    };

    private static readonly MethodInfo GenericInvoke = typeof(DaemonAuthTests)
        .GetMethod(nameof(InvokeDeniedGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task InvokeDeniedAsync(CallInvoker invoker, MethodDescriptor descriptor, Metadata headers)
    {
        var type = descriptor switch
        {
            { IsClientStreaming: false, IsServerStreaming: false } => MethodType.Unary,
            { IsClientStreaming: false, IsServerStreaming: true } => MethodType.ServerStreaming,
            { IsClientStreaming: true, IsServerStreaming: false } => MethodType.ClientStreaming,
            _ => MethodType.DuplexStreaming,
        };

        var generic = GenericInvoke.MakeGenericMethod(descriptor.InputType.ClrType, descriptor.OutputType.ClrType);
        return (Task)generic.Invoke(null, new object[]
        {
            invoker, descriptor.Service.FullName, descriptor.Name, type, headers,
        })!;
    }

    private static async Task InvokeDeniedGeneric<TReq, TResp>(
        CallInvoker invoker, string service, string methodName, MethodType type, Metadata headers)
        where TReq : class, IMessage<TReq>, new()
        where TResp : class, IMessage<TResp>, new()
    {
        var reqParser = new MessageParser<TReq>(() => new TReq());
        var respParser = new MessageParser<TResp>(() => new TResp());
        var method = new Method<TReq, TResp>(type, service, methodName,
            Marshallers.Create(m => m.ToByteArray(), reqParser.ParseFrom),
            Marshallers.Create(m => m.ToByteArray(), respParser.ParseFrom));
        var options = new CallOptions(headers);
        var request = new TReq();

        switch (type)
        {
            case MethodType.Unary:
                await invoker.AsyncUnaryCall(method, null, options, request).ResponseAsync;
                break;
            case MethodType.ServerStreaming:
                using (var call = invoker.AsyncServerStreamingCall(method, null, options, request))
                {
                    await call.ResponseStream.MoveNext(CancellationToken.None);
                }

                break;
            case MethodType.ClientStreaming:
                using (var call = invoker.AsyncClientStreamingCall(method, null, options))
                {
                    await call.ResponseAsync;
                }

                break;
            default:
                using (var call = invoker.AsyncDuplexStreamingCall(method, null, options))
                {
                    await call.ResponseStream.MoveNext(CancellationToken.None);
                }

                break;
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static string TempToken()
        => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gitloom-tok-" + Guid.NewGuid().ToString("N"), "daemon.token");
}
