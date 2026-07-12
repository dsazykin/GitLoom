using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
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

        // P2-06: one substrate facade resolved per platform; RepoSyncGrpcService obtains the
        // provisioner/worktree manager and the resolved sync remote from it. WSL2 for now.
        builder.Services.AddSingleton<IAgentEnvironment>(_ => new Wsl2AgentEnvironment());

        // Interim P2-03: no PTY factory is bound (agent processes arrive with the P2-09 lifecycle),
        // so the terminal attach echoes until a factory is supplied. The wiring tests replace this
        // singleton with a real-PTY factory to exercise the TerminalStreamer path end-to-end.
        builder.Services.AddSingleton<TerminalSessionManager>();

        builder.Services.AddGrpc(o =>
        {
            // EVERY RPC is authenticated (no public-method allowlist) and access-logged
            // through the secret field mask. Order: authenticate, then log.
            o.Interceptors.Add<BearerTokenInterceptor>();
            o.Interceptors.Add<SecretMaskingInterceptor>();
        });
    }

    /// <summary>Maps the four gRPC services. Shared by entry point and tests.</summary>
    public static void MapServices(WebApplication app)
    {
        app.MapGrpcService<AgentGrpcService>();
        app.MapGrpcService<TerminalGrpcService>();
        app.MapGrpcService<RepoSyncGrpcService>();
        app.MapGrpcService<GatewayGrpcService>();
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
