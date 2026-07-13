using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using GitLoom.Protos.V1;
using GitLoom.Server.Auth;
using GitLoom.Server.Runtime;
using GitLoom.Server.Tests.Fixtures;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace GitLoom.Server.Tests;

/// <summary>
/// TI-P2-03 §4–5 — the daemon Attach wired to a real PTY through the <see cref="Terminal.TerminalStreamer"/>.
/// The default daemon has no PTY factory (interim: agents bind in P2-09), so here we override
/// <see cref="TerminalSessionManager"/> with a real-PTY factory and assert bytes round-trip through
/// the full gRPC bidi + streamer path. Unix uses <c>/bin/cat</c> echo (Linux-only); Windows uses a
/// ConPTY probe. The authoritative Linux run is the Docker/Linux CI leg.
/// </summary>
public sealed class TerminalPtyAttachTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public TerminalPtyAttachTests(DaemonFixture daemon) => _daemon = daemon;

    [LinuxOnlyFact]
    public async Task Attach_WithPtyFactory_ShouldRoundTripCatEcho_ThroughStreamer()
    {
        using var host = _daemon.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new TerminalSessionManager(
                    _ => PtyProcessShim.Spawn("/bin/cat", Array.Empty<string>(), Path.GetTempPath(), Env(), 80, 24)));
            }));

        var received = await AttachAndCollectAsync(host, "agent-cat",
            call => call.RequestStream.WriteAsync(new TerminalInput { Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes("gitloom-daemon-echo\n")) }),
            s => s.Contains("gitloom-daemon-echo"));

        Assert.Contains("gitloom-daemon-echo", received);
    }

    [WindowsOnlyFact]
    public async Task Attach_WithPtyFactory_ShouldStreamConPtyOutput()
    {
        var whoami = Path.Combine(Environment.SystemDirectory, "whoami.exe");
        using var host = _daemon.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(new TerminalSessionManager(
                    _ => PtyProcessShim.Spawn(whoami, Array.Empty<string>(), Path.GetTempPath(), Env(), 80, 24)));
            }));

        var received = await AttachAndCollectAsync(host, "agent-whoami",
            _ => Task.CompletedTask,
            s => s.Trim().Length > 0);

        Assert.False(string.IsNullOrWhiteSpace(received));
    }

    private static async Task<string> AttachAndCollectAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
        string agentId,
        Func<AsyncDuplexStreamingCall<TerminalInput, TerminalOutput>, Task> afterAttach,
        Func<string, bool> until)
    {
        var token = host.Services.GetRequiredService<SessionTokenFile>().Token;
        var channel = GrpcChannel.ForAddress(
            host.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var client = new TerminalService.TerminalServiceClient(channel);

        var metadata = new Metadata { { "authorization", $"bearer {token}" } };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var call = client.Attach(metadata, cancellationToken: cts.Token);

        await call.RequestStream.WriteAsync(new TerminalInput { AgentId = agentId });
        await afterAttach(call);

        var sb = new StringBuilder();
        try
        {
            await foreach (var output in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                if (output.FrameCase == TerminalOutput.FrameOneofCase.Raw)
                {
                    sb.Append(Encoding.UTF8.GetString(output.Raw.ToByteArray()));
                    if (until(sb.ToString()))
                    {
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out — the caller asserts on what arrived.
        }
        catch (RpcException)
        {
            // Stream torn down as we stopped reading.
        }

        return sb.ToString();
    }

    private static IReadOnlyDictionary<string, string> Env()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables())
        {
            if (kv.Key is string k && kv.Value is string v)
            {
                env[k] = v;
            }
        }

        env["TERM"] = "xterm-256color";
        return env;
    }
}
