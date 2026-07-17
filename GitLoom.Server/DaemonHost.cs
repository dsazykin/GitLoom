using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Sandbox;
using GitLoom.Core.Audit;
using GitLoom.Protos.V1;
using GitLoom.Server.Auth;
using GitLoom.Server.Logging;
using GitLoom.Server.Runtime;
using GitLoom.Server.Services;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitLoom.Server;

/// <summary>
/// Builds and runs the daemon host. Kept separate from the entry point so both the
/// real <c>Program</c> and the in-proc <c>WebApplicationFactory&lt;Program&gt;</c> test
/// tier share one configuration path, and the port-bound test can start a real host.
/// </summary>
public static class DaemonHost
{
    /// <summary>
    /// Configures services + interceptors + the gRPC service map on an existing builder.
    /// Shared by the entry point and by <see cref="WebApplicationFactory"/>-based tests.
    /// </summary>
    public static void ConfigureServices(WebApplicationBuilder builder, DaemonOptions options)
    {
        // The daemon is silent on stdout/stderr (G-13 "prints nothing"; the CI smoke
        // asserts a quiet start). Access logs / the secret-mask formatter still reach
        // any provider a test attaches after this (the log-capture sink).
        builder.Logging.ClearProviders();

        // Session token: created user-only-readable on disk; the interceptor compares
        // against it. The in-proc test tier isolates the path via Daemon:TokenPath
        // (env Daemon__TokenPath) and reads the created token back from that file.
        var tokenPath = options.TokenPath ?? builder.Configuration["Daemon:TokenPath"];
        var tokenFile = SessionTokenFile.Create(tokenPath);
        builder.Services.AddSingleton(tokenFile);

        builder.Services.AddSingleton<IAuditLog, InMemoryAuditLog>();
        builder.Services.AddSingleton<AgentSessionStore>();

        // P2-14 governance spine: role registry + terminal-lock registry (the RoleInterceptor enforces
        // both daemon-side), the daemon-derived approver-identity resolver (SA-1/F2), the plan-approval
        // service (restart-safe JSON store next to the session token), the shared kill-switch freeze gate
        // (SA-1/F4 — merge/spawn consult it), and the kill switch itself.
        builder.Services.AddSingleton<Auth.ConnectionRoleRegistry>();
        builder.Services.AddSingleton<Auth.TerminalLockRegistry>();
        builder.Services.AddSingleton<Auth.IApproverIdentityResolver, Auth.PeerCredentialIdentityResolver>();
        builder.Services.AddSingleton(sp => new Core.Agents.Orchestrator.PlanApprovalService(
            store: new Core.Agents.Orchestrator.JsonPlanApprovalStore(ResolvePlanStorePath(tokenPath)),
            audit: sp.GetRequiredService<IAuditLog>()));
        // P2-47 #9: the coordinator conversation the CoordinatorService streams. Registered with no reply
        // engine in the shipped daemon — the live LLM-backed CoordinatorAgent adapter is the one leg that
        // needs a real model (the documented un-verifiable leg); the transcript store + streaming are real
        // regardless, and the in-proc test injects a real CoordinatorAgent-backed engine to drive it.
        builder.Services.AddSingleton(_ => new Core.Agents.Orchestrator.CoordinatorConversationService());

        builder.Services.AddSingleton<Core.Agents.Orchestrator.KillSwitchGate>();
        builder.Services.AddSingleton<Core.Agents.Orchestrator.IKillTarget, Runtime.SessionStoreKillTarget>();
        builder.Services.AddSingleton(sp => new Core.Agents.Orchestrator.KillSwitch(
            gate: sp.GetRequiredService<Core.Agents.Orchestrator.KillSwitchGate>(),
            target: sp.GetRequiredService<Core.Agents.Orchestrator.IKillTarget>(),
            audit: sp.GetRequiredService<IAuditLog>()));

        // P2-07: the network-transparency sink (P2-17 supplies the persisted/streamed impl). The
        // egress proxy + daemon git proxy record every fetch/verdict here; the allowlist change log
        // rides the IAuditLog above.
        builder.Services.AddSingleton<INetworkTransparencyLog, InMemoryNetworkTransparencyLog>();

        // P2-06/P2-07: one substrate facade resolved per platform; RepoSyncGrpcService obtains the
        // provisioner/worktree manager, and the P2-07 spawn path obtains the hardened sandbox engine +
        // default-deny egress policy, from it. WSL2 for now. (The A6 DaemonGitProxy is constructed
        // per-repo from its allowlisted prefixes when the sandbox spawn path wires it in.)
        builder.Services.AddSingleton<IAgentEnvironment>(sp =>
            new Wsl2AgentEnvironment(auditLog: sp.GetRequiredService<IAuditLog>()));

        // P2-47 #8: the real sandboxed-spawn chain behind AgentService.SpawnAgent (provision worktree →
        // ensure default-deny egress → start hardened jail). Kept out of the gRPC class (validation+dispatch
        // only); degrades to a session-only record when the repo handle is not provisioned.
        // The installed-CLI catalog is shared (launcher + the ListInstalledAdapters RPC); it reads the
        // VM registry fresh per call, so a singleton carries no staleness.
        builder.Services.AddSingleton<Core.Agents.Adapters.InstalledAdapterCatalog>();
        builder.Services.AddSingleton<Runtime.SandboxAgentLauncher>();

        // Tier-1 daemon fast-path: the GetDaemonInfo skew probe's data source (daemon assembly
        // version + the /etc/gitloomos-release payload stamp). Instance-registered so the default
        // release-file path applies; tests override with a temp-file provider.
        builder.Services.AddSingleton(new Runtime.DaemonInfoProvider());

        // PR3 (CLI-as-coordinator): the spawn workflow shared by the RPC and the coordinator's in-jail
        // gitloom-agent channel — CLI-under-TTY binding (AgentCliBinder → TerminalSessionManager +
        // SessionLeader), the per-coordinator Unix-socket IPC server (endpoint dirs next to the
        // (test-isolated) session token), and the memory-only per-kind key cache.
        builder.Services.AddSingleton<Runtime.SessionKeyCache>();
        builder.Services.AddSingleton<Runtime.AgentCliBinder>();
        builder.Services.AddSingleton(new Runtime.CoordinatorIpcServer(ResolveAgentIpcRoot(tokenPath)));
        builder.Services.AddSingleton<Runtime.AgentSpawnService>();

        // P2-47 #7: the merge-diff bridge behind MergeQueueService.GetMergeDiff — the agent-branch-vs-main
        // diff the review cockpit renders (StreamQueue doesn't carry it). Reuses the audited git path +
        // pure PatchParser over the daemon's bare mirror.
        builder.Services.AddSingleton<Core.Agents.Orchestrator.IMergeBranchDiffService>(sp =>
            new Core.Agents.Orchestrator.MergeBranchDiffService(sp.GetRequiredService<IAgentEnvironment>().Repos));

        // Terminal sessions: agents launched with an installed CLI get a long-lived BOUND session
        // (AgentCliBinder → docker exec under a real PTY) that Attach streams with replay across
        // re-attaches. The per-attach factory ctor remains the TI-P2-03 wiring-test shape; with
        // neither, the attach falls back to the P2-02 echo.
        builder.Services.AddSingleton<TerminalSessionManager>();

        // P2-09: the session leader owns the per-agent PTY fds and the durable, leader-owned registry
        // the daemon reattaches through on boot (no daemon-side pidfiles). The registry lives next to
        // the (test-isolated) session token so each in-proc host gets its own.
        var leaderRegistryPath = ResolveLeaderRegistryPath(tokenPath);
        builder.Services.AddSingleton(new Core.Agents.Orchestrator.LeaderRegistry(leaderRegistryPath));
        builder.Services.AddSingleton<Core.Agents.Orchestrator.SessionLeader>();

        // P2-08: the AI gateway (token bucket + budgets + admission + boot reconciler). Persisted to
        // the daemon SQLite DB when available, in-memory otherwise so the daemon always starts. The DB
        // sits next to the (test-isolated) session token so each in-proc host gets its own DB.
        Gateway.GatewayServiceRegistration.Register(builder, ResolveDataPath(options, builder.Configuration, tokenPath));

        builder.Services.AddGrpc(o =>
        {
            // EVERY RPC is authenticated (no public-method allowlist), then role/terminal-lock enforced
            // (P2-14 — coordinator denied merge/approval RPCs, locked-worker input severed), then
            // access-logged through the secret field mask. Order: authenticate, authorize, log.
            o.Interceptors.Add<BearerTokenInterceptor>();
            o.Interceptors.Add<RoleInterceptor>();
            o.Interceptors.Add<SecretMaskingInterceptor>();
        });
    }

    /// <summary>
    /// The daemon SQLite path for the P2-08 spend ledger. Explicit <see cref="DaemonOptions.DataPath"/>
    /// or <c>Daemon:DataPath</c> wins; otherwise it sits next to the session token (so the in-proc test
    /// tier's per-host temp token dir also isolates the DB); otherwise the OS app-data default.
    /// </summary>
    private static string ResolveDataPath(DaemonOptions options, Microsoft.Extensions.Configuration.IConfiguration config, string? tokenPath)
    {
        var explicitPath = options.DataPath ?? config["Daemon:DataPath"];
        if (!string.IsNullOrEmpty(explicitPath))
        {
            return explicitPath;
        }

        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "gitloom-daemon.db");
            }
        }

        // GitLoomPaths, not GetFolderPath: the latter returns "" on Unix for a not-yet-materialized
        // home subdir — this fallback must never yield a relative path under a service context.
        return Path.Combine(GitLoom.Core.GitLoomPaths.DataRoot(), "gitloom-daemon.db");
    }

    /// <summary>
    /// The P2-09 leader-registry path: next to the (test-isolated) session token so each in-proc host
    /// gets its own leader-owned state; otherwise the OS app-data default.
    /// </summary>
    private static string ResolveLeaderRegistryPath(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "gitloom-leader-sessions.json");
            }
        }

        return Path.Combine(GitLoom.Core.GitLoomPaths.DataRoot(), "gitloom-leader-sessions.json");
    }

    /// <summary>
    /// The per-coordinator agent-IPC root (Unix sockets + spawn shims): next to the (test-isolated)
    /// session token so each in-proc host gets its own; otherwise the OS app-data default. On the VM
    /// this is an ext4 path (a G-11-legal mount source).
    /// </summary>
    private static string ResolveAgentIpcRoot(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "agent-ipc");
            }
        }

        return Path.Combine(GitLoom.Core.GitLoomPaths.DataRoot(), "agent-ipc");
    }

    /// <summary>
    /// The P2-14 plan-approval JSON store path: next to the (test-isolated) session token so each in-proc
    /// host gets its own restart-safe store; otherwise the OS app-data default.
    /// </summary>
    private static string ResolvePlanStorePath(string? tokenPath)
    {
        if (!string.IsNullOrEmpty(tokenPath))
        {
            var dir = Path.GetDirectoryName(tokenPath);
            if (!string.IsNullOrEmpty(dir))
            {
                return Path.Combine(dir, "gitloom-plans.json");
            }
        }

        return Path.Combine(GitLoom.Core.GitLoomPaths.DataRoot(), "gitloom-plans.json");
    }

    /// <summary>Maps the gRPC services. Shared by entry point and tests.</summary>
    public static void MapServices(WebApplication app)
    {
        app.MapGrpcService<AgentGrpcService>();
        app.MapGrpcService<TerminalGrpcService>();
        app.MapGrpcService<RepoSyncGrpcService>();
        app.MapGrpcService<GatewayGrpcService>();
        app.MapGrpcService<MergeQueueGrpcService>();
        app.MapGrpcService<PlanApprovalGrpcService>();
        app.MapGrpcService<KillSwitchGrpcService>();
        app.MapGrpcService<CoordinatorGrpcService>();
    }

    /// <summary>
    /// Builds a real (Kestrel) daemon host bound to loopback only on
    /// <see cref="DaemonOptions.Port"/>. Never binds a wildcard / non-loopback
    /// address (invariant 2).
    /// </summary>
    public static WebApplication Build(DaemonOptions options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Loopback only. HTTP/2 cleartext (h2c) — the session token, not TLS, is the
            // loopback trust boundary.
            kestrel.Listen(IPAddress.Loopback, options.Port,
                listen => listen.Protocols = HttpProtocols.Http2);
        });

        ConfigureServices(builder, options);
        var app = builder.Build();
        MapServices(app);
        return app;
    }

    /// <summary>
    /// Starts a real daemon host, mapping a bind failure (port already in use) to a
    /// typed <see cref="DaemonStartupException"/> naming the port.
    /// </summary>
    public static async Task<WebApplication> StartAsync(DaemonOptions options, CancellationToken ct = default)
    {
        var app = Build(options);
        try
        {
            await app.StartAsync(ct);
        }
        catch (IOException ex)
        {
            await app.DisposeAsync();
            throw new DaemonStartupException(options.Port,
                $"GitLoom daemon could not bind loopback port {options.Port} (already in use?).", ex);
        }

        return app;
    }

    /// <summary>
    /// The <c>--local-dev --smoke</c> path: start, self-probe an authenticated
    /// <c>ListAgents</c> over the loopback endpoint, exit. Prints nothing on success;
    /// returns a non-zero code on failure.
    /// </summary>
    public static async Task<int> RunSmokeAsync(DaemonOptions options)
    {
        // h2c client support for the loopback probe.
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await using var app = await StartAsync(options);
        var tokenFile = app.Services.GetRequiredService<SessionTokenFile>();

        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{options.Port}");
        var client = new AgentService.AgentServiceClient(channel);
        var metadata = new Grpc.Core.Metadata { { "authorization", $"bearer {tokenFile.Token}" } };
        var deadline = DateTime.UtcNow.AddSeconds(10);

        await client.ListAgentsAsync(new ListAgentsRequest(), metadata, deadline);

        await app.StopAsync();
        return 0;
    }
}
