using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using GitLoom.Core;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Sandbox;
using GitLoom.Core.Audit;
using GitLoom.Server.Runtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GitLoom.Server.Gateway;

/// <summary>
/// Wires the P2-08 gateway stack into the daemon host: the token-bucket <see cref="AiGateway"/>, the
/// persisted <see cref="BudgetLedger"/>, the <see cref="AdmissionController"/>, and the boot
/// <see cref="SwarmReconciler"/> (run through the RT-D1 ordered <see cref="DaemonBootSequence"/>). All
/// persistence is best-effort: if the daemon SQLite DB cannot be opened/migrated the stack falls back
/// to in-memory stores so the daemon still starts (the gRPC surface must never fail to bind on a DB
/// hiccup).
/// </summary>
public static class GatewayServiceRegistration
{
    public static void Register(WebApplicationBuilder builder, string dbPath)
    {
        var services = builder.Services;

        // Best-effort DB-backed persistence; in-memory fallback keeps the daemon startable.
        ISpendStore spendStore;
        IExpectedAgentStore expectedStore;
        IBudgetStore budgetStore;
        if (TryPrepareDatabase(dbPath, out var factory))
        {
            spendStore = new DbSpendStore(factory);
            expectedStore = new DbExpectedAgentStore(factory);
            budgetStore = new DbBudgetStore(factory);
        }
        else
        {
            spendStore = new InMemorySpendStore();
            expectedStore = new InMemoryExpectedAgentStore();
            budgetStore = new InMemoryBudgetStore();
        }

        Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;

        // Register the store instances behind their interfaces so a test host can override them (isolated
        // in-memory) via ConfigureTestServices; everything downstream resolves them from DI.
        services.AddSingleton(spendStore);
        services.AddSingleton(expectedStore);
        services.AddSingleton(budgetStore);

        services.AddSingleton(sp =>
        {
            var stored = sp.GetRequiredService<IBudgetStore>().Get();
            var caps = new BudgetCaps(stored.TokenCap, stored.UsdMicrosCap, 0, 0);
            return new BudgetLedger(sp.GetRequiredService<ISpendStore>(), clock, caps);
        });

        // Supervisor: the PTY-pause / ListAgents-metadata wiring is completed by the P2-09 lifecycle;
        // until a worker PTY is bound the daemon uses the no-op supervisor (the pause/resume behavior
        // is fully exercised through the test fake).
        services.AddSingleton<IAgentSupervisor>(NullAgentSupervisor.Instance);

        services.AddSingleton(sp => new AiGateway(
            TokenBucket.FromKeyHealth(null, clock),
            sp.GetRequiredService<BudgetLedger>(),
            sp.GetRequiredService<IAgentSupervisor>(),
            sp.GetRequiredService<IAuditLog>(),
            clock));

        // Admission control: current-agent count comes from the live session store; the /proc/meminfo
        // sampler is the default (real on the WSL2 VM, "unknown → admit" on a Windows dev box).
        services.AddSingleton(sp => new AdmissionController(
            runningAgentCount: () => sp.GetRequiredService<AgentSessionStore>().List().Count,
            clock: clock));

        services.AddSingleton(sp => new SwarmReconciler(
            listContainers: BuildContainerLister(),
            expected: sp.GetRequiredService<IExpectedAgentStore>(),
            worktrees: sp.GetRequiredService<IAgentEnvironment>().Worktrees,
            policy: OrphanPolicy.Adopt));

        services.AddSingleton(sp => DaemonBootSequence.Build(sp.GetRequiredService<SwarmReconciler>()));

        services.AddHostedService<GatewayHostedService>();
    }

    private static bool TryPrepareDatabase(string dbPath, out Func<AppDbContext> factory)
    {
        try
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using (var db = new AppDbContext(dbPath))
            {
                db.Database.Migrate();
            }

            factory = () => new AppDbContext(dbPath);
            return true;
        }
        catch (Exception)
        {
            factory = null!;
            return false;
        }
    }

    /// <summary>
    /// A best-effort Docker lister for the reconciler: real listing on a Docker host, an empty listing
    /// when Docker is unreachable (Windows dev box / Docker-less CI) so boot never fails.
    /// </summary>
    private static Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> BuildContainerLister()
    {
        return async ct =>
        {
            try
            {
                using var docker = new DockerClientConfiguration().CreateClient();
                return await DockerAgentLister.ListAsync(docker, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return Array.Empty<AgentContainerState>();
            }
        };
    }
}
