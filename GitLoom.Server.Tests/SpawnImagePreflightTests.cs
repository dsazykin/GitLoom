using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Sandbox;
using GitLoom.Protos.V1;
using GitLoom.Server.Auth;
using GitLoom.Server.Logging;
using GitLoom.Server.Tests.Fixtures;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace GitLoom.Server.Tests;

/// <summary>
/// The v1 spawn preflight (field failure 2026-07-17, twice: a fresh GitLoomEnv import AND the
/// tier-2 VM upgrade both leave the docker image store empty). BEFORE any worktree/jail is made,
/// <c>SandboxAgentLauncher</c> verifies BOTH jail images and a missing one maps to an actionable
/// <c>FailedPrecondition</c> naming exactly that image — which finally makes the egress-proxy
/// absence (previously an opaque failure inside the egress setup) actionable too. Both-present
/// proceeds to the engine untouched.
/// </summary>
public sealed class SpawnImagePreflightTests : IClassFixture<DaemonFixture>
{
    private const string RepoHandle = "repo-preflight";

    private readonly DaemonFixture _daemon;

    public SpawnImagePreflightTests(DaemonFixture daemon) => _daemon = daemon;

    [Fact]
    public async Task Spawn_BothImagesPresent_ProceedsToTheEngine()
    {
        using var rig = Rig(missingImage: null);

        var agentId = await SpawnAsync(rig);

        Assert.False(string.IsNullOrWhiteSpace(agentId));
        Assert.Equal(1, rig.Environment.Engine.SpawnCalls);

        // The Spawn category records the step sequence: launch begin → preflight ok → jail started.
        var logs = _daemon.CapturedLogs;
        Assert.Contains(logs, l => IsSpawn(l) && l.Contains("launch begin", StringComparison.Ordinal));
        Assert.Contains(logs, l => IsSpawn(l) && l.Contains("preflight ok", StringComparison.Ordinal));
        Assert.Contains(logs, l => IsSpawn(l) && l.Contains("jail started", StringComparison.Ordinal));
    }

    private static bool IsSpawn(string line) =>
        line.Contains("[" + DaemonLogCategories.Spawn + "]", StringComparison.Ordinal);

    [Fact]
    public async Task Spawn_MissingAgentBaseImage_IsFailedPrecondition_NamingItAndTheRepair()
    {
        using var rig = Rig(missingImage: "gitloom-agent-base:latest");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("gitloom-agent-base", ex.Status.Detail);
        Assert.Contains("restart Mainguard", ex.Status.Detail);
        Assert.Contains("docker build", ex.Status.Detail);
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls); // preflight fires before any jail work

        // The Spawn category records the preflight failure naming the missing image.
        Assert.Contains(_daemon.CapturedLogs, l =>
            IsSpawn(l) && l.Contains("preflight failed", StringComparison.Ordinal)
            && l.Contains("gitloom-agent-base", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Spawn_MissingEgressProxyImage_IsFailedPrecondition_NamingIt()
    {
        // The previously NOT-actionable path: the egress image's absence used to fail opaquely
        // inside EgressPolicy.EnsureReadyAsync, not at container-create.
        using var rig = Rig(missingImage: "gitloom-egress-proxy:latest");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("gitloom-egress-proxy", ex.Status.Detail);
        Assert.DoesNotContain("gitloom-agent-base", ex.Status.Detail); // names exactly the absent one
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls);
    }

    [Fact]
    public async Task Spawn_StaleAgentBaseImage_IsFailedPrecondition_NamingItAsOutdated()
    {
        // Present but version-skewed: its gitloom.image.version label ≠ the daemon's expected constant
        // (a Dockerfile change left old bytes under the same tag) — the skew presence alone can't see.
        using var rig = Rig(missingImage: null, staleImage: "gitloom-agent-base:latest");

        var ex = await Assert.ThrowsAsync<RpcException>(() => SpawnAsync(rig));

        Assert.Equal(StatusCode.FailedPrecondition, ex.StatusCode);
        Assert.Contains("gitloom-agent-base", ex.Status.Detail);
        Assert.Contains("outdated", ex.Status.Detail);
        Assert.Equal(0, rig.Environment.Engine.SpawnCalls);

        // The Spawn category records the preflight failure naming the STALE image.
        Assert.Contains(_daemon.CapturedLogs, l =>
            IsSpawn(l) && l.Contains("preflight failed", StringComparison.Ordinal)
            && l.Contains("gitloom-agent-base", StringComparison.Ordinal)
            && l.Contains("stale", StringComparison.Ordinal));
    }

    // ---- rig (SpawnErrorMappingTests' in-proc pattern, engine scripted per image) -------------

    private async Task<string> SpawnAsync(PreflightRig rig)
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

    private PreflightRig Rig(string? missingImage, string? staleImage = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "gl-preflight-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(tempRoot, "repos", RepoHandle)); // "provisioned" → jail path
        var environment = new PreflightEnvironment(tempRoot, missingImage, staleImage);
        var host = _daemon.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IAgentEnvironment>(environment)));
        return new PreflightRig(tempRoot, host, environment);
    }

    private sealed class PreflightRig : IDisposable
    {
        private readonly string _tempRoot;
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> _host;

        public PreflightRig(
            string tempRoot, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> host,
            PreflightEnvironment environment)
        {
            _tempRoot = tempRoot;
            _host = host;
            Environment = environment;
        }

        public PreflightEnvironment Environment { get; }

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

    /// <summary>A provisioned-looking substrate whose engine reports one scripted image as absent
    /// (null = both present) and records whether the jail spawn was ever reached.</summary>
    internal sealed class PreflightEnvironment : IAgentEnvironment
    {
        public PreflightEnvironment(string root, string? missingImage, string? staleImage = null)
        {
            Engine = new ImageAwareEngine(missingImage, staleImage);
            Repos = new StubProvisioner(root);
            Worktrees = new StubWorktrees(root);
        }

        public ImageAwareEngine Engine { get; }

        public string SubstrateId => "fake";

        public SubstrateCapabilities Capabilities { get; } = new(false, false, "none", "test");

        public IRepoProvisioner Repos { get; }

        public IAgentWorktreeManager Worktrees { get; }

        public ISandboxEngine Sandboxes => Engine;

        public IEgressPolicy Egress { get; } = new StubEgress();

        public SyncRemote ResolveSyncRemote(string repoHash) => new("fake-remote", $"fake://{repoHash}");

        internal sealed class ImageAwareEngine : ISandboxEngine
        {
            private readonly string? _missingImage;
            private readonly string? _staleImage;
            private int _spawnCalls;

            public ImageAwareEngine(string? missingImage, string? staleImage = null)
            {
                _missingImage = missingImage;
                _staleImage = staleImage;
            }

            public int SpawnCalls => _spawnCalls;

            public Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct = default) =>
                Task.FromResult(!string.Equals(imageRef, _missingImage, StringComparison.Ordinal));

            // A stale image reports a wrong label; every other present image reports the expected one,
            // so present+current proceeds while a label mismatch is a typed FailedPrecondition.
            public Task<string?> ImageVersionAsync(string imageRef, CancellationToken ct = default) =>
                Task.FromResult(string.Equals(imageRef, _staleImage, StringComparison.Ordinal)
                    ? "an-old-source-hash"
                    : SandboxImageVersions.For(imageRef));

            public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default)
            {
                Interlocked.Increment(ref _spawnCalls);
                return Task.FromResult(new SandboxHandle("jail-" + request.AgentId, Reused: false));
            }

            public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default) =>
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

            public IReadOnlyList<Mainguard.Git.Models.WorktreeItem> List(string repoHash) =>
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
