using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using GitLoom.Core;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Orchestrator;
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
        IMergeLeaseStore mergeLeaseStore;
        Func<AppDbContext>? dbFactory = null;
        if (TryPrepareDatabase(dbPath, out var factory))
        {
            dbFactory = factory;
            spendStore = new DbSpendStore(factory);
            expectedStore = new DbExpectedAgentStore(factory);
            budgetStore = new DbBudgetStore(factory);
            mergeLeaseStore = new DbMergeLeaseStore(factory);
        }
        else
        {
            spendStore = new InMemorySpendStore();
            expectedStore = new InMemoryExpectedAgentStore();
            budgetStore = new InMemoryBudgetStore();
            mergeLeaseStore = new InMemoryMergeLeaseStore();
        }

        Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;

        // Register the store instances behind their interfaces so a test host can override them (isolated
        // in-memory) via ConfigureTestServices; everything downstream resolves them from DI.
        services.AddSingleton(spendStore);
        services.AddSingleton(expectedStore);
        services.AddSingleton(budgetStore);
        services.AddSingleton(mergeLeaseStore);

        // P2-10 merge queue: the registry the gRPC service resolves per-repo queues through (populated as
        // repos' swarms come up). Empty at boot — an unknown handle is a typed NOT_FOUND.
        services.AddSingleton<IMergeQueueRegistry, MergeQueueRegistry>();

        services.AddSingleton(sp =>
        {
            var stored = sp.GetRequiredService<IBudgetStore>().Get();
            var caps = new BudgetCaps(stored.TokenCap, stored.UsdMicrosCap, 0, 0);
            return new BudgetLedger(sp.GetRequiredService<ISpendStore>(), clock, caps);
        });

        // Supervisor: P2-09 wires the REAL supervisor — the gateway's 429 / budget pause now drives a
        // real PTY input pause through the session leader and reflects the agent state in the session
        // store (streamed to clients as an AgentEvent state change), replacing NullAgentSupervisor.
        services.AddSingleton<IAgentSupervisor>(sp => new PtyAgentSupervisor(
            sp.GetRequiredService<SessionLeader>(),
            sp.GetRequiredService<AgentSessionStore>()));

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

        // Boot order: merge-reconcile (RT-D1, FIRST — before admission) → swarm (container) reconcile →
        // P2-09 leader reattach (containers → leaders → PTY reattach; mismatches resolved toward Docker
        // truth). The merge-reconcile slot now carries the real RT-D1 journal-replay task (§3.7): for any
        // repo with an outstanding lease it replays the T-19 journal and synthesizes a missing
        // ConfirmMerge before any new BeginMerge is accepted.
        services.AddSingleton(sp =>
        {
            var registry = sp.GetRequiredService<IMergeQueueRegistry>();
            IBootTask mergeReconcile = new MergeReconcileTask(
                leases: sp.GetRequiredService<IMergeLeaseStore>(),
                journal: dbFactory is null
                    ? new Core.Services.NullOperationJournal()
                    : new Core.Services.OperationJournal(dbFactory),
                resolveRepoPath: _ => null, // repos map in as their swarms come up; none at boot.
                onMerged: (agentId, postSha) =>
                {
                    // Fire the stale cascade on whichever active queue owns this agent (best-effort).
                    foreach (var handle in Array.Empty<string>())
                    {
                        registry.Resolve(handle)?.Queue.ConfirmHumanMerge(agentId, postSha);
                    }
                });

            return DaemonBootSequence.Build(
                sp.GetRequiredService<SwarmReconciler>(),
                mergeReconcile: mergeReconcile,
                leaderReattach: new LeaderReattachTask(sp.GetRequiredService<SessionLeader>(), BuildContainerLister()));
        });

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
