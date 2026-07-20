using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Protos.V1;
using Mainguard.Server.Logging;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Mainguard.Server.Tests;

/// <summary>
/// TI-P2-02 §8 / plan §6 row 5 — the G-13 field-mask. A <c>// SECRET</c> field never
/// appears in captured logs (value, length, or prefix), and every <c>// SECRET</c> proto
/// field is registered in <see cref="SecretFieldMask"/>.
/// </summary>
public sealed class LoggingMaskTests : IClassFixture<DaemonFixture>
{
    private readonly DaemonFixture _daemon;

    public LoggingMaskTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task SecretMaskedField_ShouldNeverAppearInLogs()
    {
        const string sentinel = "SUPER-SECRET-KEY-DEADBEEF-DO-NOT-LOG-0123456789";

        var client = new AgentService.AgentServiceClient(_daemon.CreateChannel());
        await client.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = "repo-handle-opaque",
            TaskPrompt = "do the thing",
            AgentKind = "claude-code",
            ModelApiKey = sentinel,
        }, _daemon.AuthHeaders());

        var logs = _daemon.CapturedLogs;
        Assert.NotEmpty(logs);
        // Zero occurrences of the secret anywhere — not even a prefix of it.
        Assert.DoesNotContain(logs, line => line.Contains("SUPER-SECRET", StringComparison.Ordinal));
        Assert.DoesNotContain(logs, line => line.Contains(sentinel, StringComparison.Ordinal));
        // The request WAS logged, with the field masked.
        Assert.Contains(logs, line => line.Contains("model_api_key=***", StringComparison.Ordinal));
    }

    // A non-RpcException that escapes a handler used to reach the client as a bare UNKNOWN with nothing
    // recorded daemon-side (the #201 class of invisibility). The interceptor now catches it, logs an
    // Error under the Rpc category naming the method + exception type, and rethrows — and the fault line
    // is secret-free (no request/response body is rendered on this path).
    [Fact]
    public async Task HandlerFault_NonRpcException_IsRecordedUnderRpc_WithMethodAndType()
    {
        using var host = _daemon.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IMergeBranchDiffService>(new ThrowingMergeDiffService())));
        var channel = GrpcChannel.ForAddress(
            host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = host.Server.CreateHandler() });
        var client = new MergeQueueService.MergeQueueServiceClient(channel);
        // This derived host regenerates its own session token, so authenticate against ITS token.
        var auth = new Metadata
        {
            { "authorization", $"bearer {host.Services.GetRequiredService<Mainguard.Server.Auth.SessionTokenFile>().Token}" },
        };

        // The handler propagates the injected InvalidOperationException (it only maps RepoProvisioning),
        // so the client sees a bare Unknown — but the daemon now recorded the fault.
        var ex = await Assert.ThrowsAsync<RpcException>(() => client.GetMergeDiffAsync(new GetMergeDiffRequest
        {
            RepoHandle = "repo-fault",
            AgentId = "agent-fault",
        }, auth).ResponseAsync);
        Assert.Equal(StatusCode.Unknown, ex.StatusCode);

        var logs = _daemon.CapturedLogs;
        Assert.Contains(logs, line =>
            line.Contains("[" + DaemonLogCategories.Rpc + "]", StringComparison.Ordinal)
            && line.Contains("rpc-fault", StringComparison.Ordinal)
            && line.Contains("GetMergeDiff", StringComparison.Ordinal)
            && line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
        // The sentinel from the mask test (same shared host lifetime) never leaked anywhere.
        Assert.DoesNotContain(logs, line => line.Contains("SUPER-SECRET", StringComparison.Ordinal));
    }

    /// <summary>An <see cref="IMergeBranchDiffService"/> whose Compute throws a non-<c>RpcException</c>,
    /// exercising the interceptor's handler-fault capture (GetMergeDiff maps only RepoProvisioning).</summary>
    private sealed class ThrowingMergeDiffService : IMergeBranchDiffService
    {
        public MergeBranchDiff Compute(string repoHash, string agentId) =>
            throw new InvalidOperationException("merge diff exploded (test)");
    }

    // The registry must cover every `// SECRET` field in the proto sources — the reviewer
    // grep, mechanized. Walks up to the repo's proto dir; fails loud if it cannot find it.
    [Fact]
    public void SecretFieldMask_ShouldCoverEverySecretProtoField()
    {
        var protoDir = FindProtoDir();
        var secretFields = EnumerateSecretFields(protoDir).ToList();
        Assert.NotEmpty(secretFields); // there is at least one // SECRET field to cover

        foreach (var (message, field) in secretFields)
        {
            Assert.True(SecretFieldMask.IsSecret($"mainguard.v1.{message}", field),
                $"proto field {message}.{field} is marked // SECRET but not registered in SecretFieldMask.");
        }
    }

    private static IEnumerable<(string Message, int Field)> EnumerateSecretFields(string protoDir)
    {
        var messageRegex = new Regex(@"^\s*message\s+(\w+)\s*\{");
        var secretFieldRegex = new Regex(@"=\s*(\d+)\s*;\s*//\s*SECRET");

        foreach (var file in Directory.EnumerateFiles(protoDir, "*.proto", SearchOption.AllDirectories))
        {
            string? currentMessage = null;
            foreach (var line in File.ReadLines(file))
            {
                var m = messageRegex.Match(line);
                if (m.Success)
                {
                    currentMessage = m.Groups[1].Value;
                }

                var s = secretFieldRegex.Match(line);
                if (s.Success && currentMessage is not null)
                {
                    yield return (currentMessage, int.Parse(s.Groups[1].Value));
                }
            }
        }
    }

    private static string FindProtoDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Mainguard.Protos", "protos");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Mainguard.Protos/protos from the test base directory.");
    }
}
