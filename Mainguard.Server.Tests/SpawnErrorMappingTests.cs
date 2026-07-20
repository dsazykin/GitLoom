using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Grpc.Core;
using Grpc.Net.Client;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using Mainguard.Protos.V1;
using Mainguard.Server.Auth;
using Mainguard.Server.Tests.Fixtures;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Mainguard.Server.Tests;

/// <summary>
/// Field failure 2026-07-17: SpawnAgent against a VM without the CI-shipped
/// <c>mainguard-agent-base</c> image surfaced as a bare <c>UNKNOWN — Exception was thrown by
/// handler</c>. The RPC must map the real causes: a missing sandbox image → actionable
/// <c>FailedPrecondition</c>; any other Docker/unexpected engine failure → <c>Internal</c>
/// carrying the real message. Never a detail-less UNKNOWN.
/// </summary>
public sealed class SpawnErrorMappingTests : IClassFixture<DaemonFixture>
{
    private const string RepoHandle = "repo-err";

    private readonly DaemonFixture _daemon;

    public SpawnErrorMappingTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task Spawn_MissingSandboxImage_IsFailedPrecondition_NamingTheImageAndRepair()
    {
        using var rig = Rig(new DockerImageNotFoundException(
            HttpStatusCode.NotFound, "No such image: mainguard-agent-base:latest"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("sandbox image", ex.Status.Detail);
        Assert.Contains("mainguard-agent-base", ex.Status.Detail);
    }

    [Fact]
    public async Task Spawn_DockerApiFailure_IsInternal_WithTheRealMessage()
    {
        using var rig = Rig(new DockerApiException(
            HttpStatusCode.InternalServerError, "driver failed programming external connectivity"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Contains("driver failed programming external connectivity", ex.Status.Detail);
    }

    [Fact]
    public async Task Spawn_UnexpectedEngineFault_IsInternal_NeverABareUnknown()
    {
        using var rig = Rig(new InvalidOperationException("the egress network vanished mid-spawn"));

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.Internal, ex.StatusCode);
        Assert.Contains("egress network vanished", ex.Status.Detail);
    }

    // ---- rig ----------------------------------------------------------------

    private async Task<string> SpawnAsync(ErrorRig rig)
    {
        var client = new AgentService.AgentServiceClient(rig.Channel);
        var response = await client.SpawnAgentAsync(new SpawnAgentRequest
        {
            RepoHandle = RepoHandle,
            AgentKind = "claude-code",
            TaskPrompt = "",
            ModelApiKey = "",
            Role = AgentRoles.Coordinator,
        }, rig.Auth, deadline: DateTime.UtcNow.AddSeconds(20));
        return response.AgentId;
    }

    private ErrorRig Rig(Exception engineFailure)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gl-spawnerr-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tempRoot, "repos", RepoHandle)); // "provisioned" → jail path
        var host = _daemon.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(new ThrowingEnvironment(tempRoot, engineFailure))));
        return new ErrorRig(tempRoot, host);
    }

    private sealed class ErrorRig : IDisposable
    {
        private readonly string _tempRoot;
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

        public ErrorRig(string tempRoot, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host)
        {
            _tempRoot = tempRoot;
            _host = host;
        }

        public GrpcChannel Channel => GrpcChannel.ForAddress(
            _host.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = _host.Server.CreateHandler() });

        public Metadata Auth => new()
        {
            { "authorization", $"bearer {_host.Services.GetRequiredService<SessionTokenFile>().Token}" },
        };

        public void Dispose()
        {
            _host.Dispose();
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>A provisioned-looking substrate whose jail spawn always throws the scripted failure.</summary>
    private sealed class ThrowingEnvironment : IAgentEnvironment
    {
        private readonly string _root;

        public ThrowingEnvironment(string root, Exception failure)
        {
            _root = root;
            Sandboxes = new ThrowingEngine(failure);
            Repos = new StubProvisioner(root);
            Worktrees = new StubWorktrees(root);
        }

        public string SubstrateId => "fake";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public IRepoProvisioner Repos { get; }

        public IAgentWorktreeManager Worktrees { get; }

        public ISandboxEngine Sandboxes { get; }

        public IEgressPolicy Egress { get; } = new StubEgress();

        public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

        private sealed class ThrowingEngine : ISandboxEngine
        {
            private readonly Exception _failure;

            public ThrowingEngine(Exception failure) => _failure = failure;

            public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) =>
                Task.FromException<SandboxHandle>(_failure);

            public Task<SandboxExecResult> ExecAsync(string containerId, System.Collections.Generic.IReadOnlyList<string> command, CancellationToken ct = default) =>
                Task.FromResult(new SandboxExecResult(0, string.Empty, string.Empty));

            public Task PauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task UnpauseAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task StopAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;

            public Task RemoveAsync(string containerId, CancellationToken ct = default) => Task.CompletedTask;
        }

        private sealed class StubProvisioner : IRepoProvisioner
        {
            private readonly string _root;

            public StubProvisioner(string root) => _root = root;

            public ProvisionResult Provision(string windowsRepoPathNormalized) =>
                throw new NotSupportedException("not exercised");

            public string BareRepoPathFor(string repoHash) => Path.Combine(_root, "repos", repoHash);
        }

        private sealed class StubWorktrees : IAgentWorktreeManager
        {
            private readonly string _root;

            public StubWorktrees(string root) => _root = root;

            public string CreateAgentWorktree(string repoHash, string agentId)
            {
                var path = Path.Combine(_root, "wt", repoHash, agentId);
                Directory.CreateDirectory(path);
                return path;
            }

            public void RemoveAgentWorktree(string repoHash, string agentId, bool force)
            {
                try
                {
                    Directory.Delete(Path.Combine(_root, "wt", repoHash, agentId), recursive: true);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            public void Prune(string repoHash)
            {
            }

            public System.Collections.Generic.IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
                Array.Empty<Mainguard.Git.Models.WorktreeItem>();
        }

        private sealed class StubEgress : IEgressPolicy
        {
            public EgressAllowlist Allowlist { get; } = EgressAllowlist.WithDefaults(new Mainguard.Git.Audit.InMemoryAuditLog());

            public string NetworkName => "fake-net";

            public string ProxyUrl => "http://fake-proxy:3128";

            public Task EnsureReadyAsync(CancellationToken ct = default) => Task.CompletedTask;

            public EgressVerdict Evaluate(string host) => EgressVerdict.Denied;
        }
    }
}
