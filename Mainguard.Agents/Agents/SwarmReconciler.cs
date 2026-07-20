using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Models;

namespace Mainguard.Agents.Agents;

/// <summary>
/// A live agent container as observed from Docker (the labels <c>gitloom.agent</c>/<c>gitloom.repo</c>
/// set by P2-07). This is the <b>only</b> liveness signal the reconciler consumes — there are no
/// PID/lock-file reads (rejection trigger).
/// </summary>
public sealed record AgentContainerState(string AgentId, string RepoHash, string ContainerId, bool Running);

/// <summary>What to do with a live container the daemon did not expect (an orphan).</summary>
public enum OrphanPolicy
{
    /// <summary>Adopt it into the expected set (default — a crash-surviving agent keeps working).</summary>
    Adopt,

    /// <summary>Stop it (a stricter posture: only daemon-launched agents may run).</summary>
    Stop,
}

/// <summary>The persistence seam for the expected-agents table — SQLite in the daemon, in-memory in tests.</summary>
public interface IExpectedAgentStore
{
    /// <summary>Every agent the daemon expected to be alive (any disposition).</summary>
    IReadOnlyList<ExpectedAgent> All();

    /// <summary>Record (or refresh) an expected agent as live.</summary>
    void Upsert(string repoHash, string agentId, string disposition);

    /// <summary>Mark an expected agent dead with the disposal reason (surfaced to the UI).</summary>
    void MarkDead(string repoHash, string agentId, string reason);
}

/// <summary>The outcome of one reconcile pass (asserted by tests, surfaced to the UI).</summary>
public sealed record ReconcileReport(
    IReadOnlyList<string> Pruned,
    IReadOnlyList<string> Adopted,
    IReadOnlyList<string> Stopped);

/// <summary>
/// P2-08 swarm reconciler. On daemon boot it makes <b>Docker the single source of truth</b> for swarm
/// state: it lists the live <c>gitloom.agent</c> containers and diffs them against the expected-agents
/// table.
/// <list type="bullet">
///   <item>Expected but no live container → prune the worktree (P2-06
///   <c>RemoveAgentWorktree(force:true)</c>) and mark the row <c>Dead</c> with a disposal reason.</item>
///   <item>Live but not expected (orphan, e.g. it survived a daemon crash) → adopt or stop per
///   <see cref="OrphanPolicy"/> (default adopt).</item>
/// </list>
/// It reads <b>no</b> process-id or on-disk liveness state. Deleting any on-disk daemon state and
/// rebooting yields the same outcome, because the truth is Docker's container list.
/// </summary>
public sealed class SwarmReconciler
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> _listContainers;
    private readonly IExpectedAgentStore _expected;
    private readonly IAgentWorktreeManager _worktrees;
    private readonly Func<string, CancellationToken, Task> _stopContainer;
    private readonly OrphanPolicy _policy;

    /// <param name="listContainers">Lists live <c>gitloom.agent</c> containers from Docker (injected for tests).</param>
    /// <param name="expected">The expected-agents table.</param>
    /// <param name="worktrees">P2-06 worktree manager (dead agents are pruned with force).</param>
    /// <param name="stopContainer">Stops an orphan container by id (used only under <see cref="OrphanPolicy.Stop"/>).</param>
    /// <param name="policy">What to do with orphan live containers (default adopt).</param>
    public SwarmReconciler(
        Func<CancellationToken, Task<IReadOnlyList<AgentContainerState>>> listContainers,
        IExpectedAgentStore expected,
        IAgentWorktreeManager worktrees,
        Func<string, CancellationToken, Task>? stopContainer = null,
        OrphanPolicy policy = OrphanPolicy.Adopt)
    {
        _listContainers = listContainers ?? throw new ArgumentNullException(nameof(listContainers));
        _expected = expected ?? throw new ArgumentNullException(nameof(expected));
        _worktrees = worktrees ?? throw new ArgumentNullException(nameof(worktrees));
        _stopContainer = stopContainer ?? ((_, _) => Task.CompletedTask);
        _policy = policy;
    }

    /// <summary>Runs one reconcile pass against the current Docker state.</summary>
    public async Task<ReconcileReport> ReconcileAsync(CancellationToken ct = default)
    {
        var containers = await _listContainers(ct).ConfigureAwait(false);
        var live = containers
            .Where(c => c.Running)
            .ToDictionary(c => c.AgentId, StringComparer.Ordinal);

        var pruned = new List<string>();
        var adopted = new List<string>();
        var stopped = new List<string>();

        // 1. Expected agents whose container is gone → prune + mark Dead.
        foreach (var agent in _expected.All())
        {
            if (string.Equals(agent.Disposition, "Dead", StringComparison.Ordinal))
            {
                continue; // already accounted for.
            }

            if (!live.ContainsKey(agent.AgentId))
            {
                TryPruneWorktree(agent.RepoHash, agent.AgentId);
                _expected.MarkDead(agent.RepoHash, agent.AgentId,
                    "Container not running at daemon boot (Docker reported no live jail).");
                pruned.Add(agent.AgentId);
            }
        }

        // 2. Live containers the daemon did not expect → adopt or stop.
        var expectedIds = new HashSet<string>(
            _expected.All().Select(a => a.AgentId), StringComparer.Ordinal);

        foreach (var container in live.Values)
        {
            if (expectedIds.Contains(container.AgentId))
            {
                continue;
            }

            if (_policy == OrphanPolicy.Adopt)
            {
                _expected.Upsert(container.RepoHash, container.AgentId, "Adopted");
                adopted.Add(container.AgentId);
            }
            else
            {
                await _stopContainer(container.ContainerId, ct).ConfigureAwait(false);
                stopped.Add(container.AgentId);
            }
        }

        return new ReconcileReport(pruned, adopted, stopped);
    }

    private void TryPruneWorktree(string repoHash, string agentId)
    {
        try
        {
            _worktrees.RemoveAgentWorktree(repoHash, agentId, force: true);
        }
        catch (Exception)
        {
            // Best-effort: a mirror may already be gone. The row is still marked Dead so the UI is honest.
        }
    }
}

/// <summary>One ordered boot step (RT-D1). Runs to completion before the next step starts.</summary>
public interface IBootTask
{
    /// <summary>A short, stable name (for logging/diagnostics).</summary>
    string Name { get; }

    /// <summary>Runs the step.</summary>
    Task RunAsync(CancellationToken ct);
}

/// <summary>
/// The RT-D1 ordered daemon boot sequence. Steps run <b>strictly in order</b>. The
/// <b>merge-reconcile slot is first</b> (master doc §3.1): once P2-10 lands, its journal replay
/// (synthesizing a missing <c>ConfirmMerge</c>) must complete before the swarm reconciler admits new
/// work or admission accepts spawns for a repo with an outstanding merge lease. The slot ships now as
/// a no-op (<see cref="MergeReconcilePlaceholderTask"/>) so the ordering seam exists before P2-10.
/// </summary>
public sealed class DaemonBootSequence
{
    private readonly IReadOnlyList<IBootTask> _tasks;

    public DaemonBootSequence(IReadOnlyList<IBootTask> tasks)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    }

    /// <summary>The ordered task names (for the RT-D1 ordering assertion).</summary>
    public IReadOnlyList<string> TaskNames => _tasks.Select(t => t.Name).ToArray();

    /// <summary>Runs every task in order; stops on the first failure (boot is fail-fast).</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var task in _tasks)
        {
            await task.RunAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds the canonical boot order: merge-reconcile (empty until P2-10) → swarm (container)
    /// reconcile → optional P2-09 leader reattach. The reconcile order is containers → leaders → PTY
    /// reattach; the leader step appends only when supplied (admission is passive — it gates spawns, it
    /// has no boot step).
    /// </summary>
    public static DaemonBootSequence Build(
        SwarmReconciler reconciler,
        IBootTask? mergeReconcile = null,
        IBootTask? leaderReattach = null)
    {
        var tasks = new List<IBootTask>
        {
            mergeReconcile ?? new MergeReconcilePlaceholderTask(),
            new SwarmReconcileTask(reconciler),
        };

        if (leaderReattach is not null)
        {
            tasks.Add(leaderReattach);
        }

        return new DaemonBootSequence(tasks);
    }
}

/// <summary>The RT-D1 merge-reconcile slot. A no-op until P2-10 supplies the journal-replay pass.</summary>
public sealed class MergeReconcilePlaceholderTask : IBootTask
{
    public string Name => "merge-reconcile";

    public Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The boot step that runs the <see cref="SwarmReconciler"/>.</summary>
public sealed class SwarmReconcileTask : IBootTask
{
    private readonly SwarmReconciler _reconciler;

    public SwarmReconcileTask(SwarmReconciler reconciler) => _reconciler = reconciler;

    public string Name => "swarm-reconcile";

    public Task RunAsync(CancellationToken ct) => _reconciler.ReconcileAsync(ct);
}

/// <summary>An in-memory <see cref="IExpectedAgentStore"/> for tests and the pre-persistence daemon path.</summary>
public sealed class InMemoryExpectedAgentStore : IExpectedAgentStore
{
    private readonly object _gate = new();
    private readonly List<ExpectedAgent> _rows = new();
    private long _nextId;

    public IReadOnlyList<ExpectedAgent> All()
    {
        lock (_gate)
        {
            return _rows.Select(Clone).ToArray();
        }
    }

    public void Upsert(string repoHash, string agentId, string disposition)
    {
        lock (_gate)
        {
            var existing = Find(repoHash, agentId);
            if (existing is null)
            {
                _rows.Add(new ExpectedAgent
                {
                    Id = ++_nextId,
                    RepoHash = repoHash,
                    AgentId = agentId,
                    Disposition = disposition,
                });
            }
            else
            {
                existing.Disposition = disposition;
                existing.DisposalReason = null;
            }
        }
    }

    public void MarkDead(string repoHash, string agentId, string reason)
    {
        lock (_gate)
        {
            var existing = Find(repoHash, agentId);
            if (existing is null)
            {
                _rows.Add(new ExpectedAgent
                {
                    Id = ++_nextId,
                    RepoHash = repoHash,
                    AgentId = agentId,
                    Disposition = "Dead",
                    DisposalReason = reason,
                });
            }
            else
            {
                existing.Disposition = "Dead";
                existing.DisposalReason = reason;
            }
        }
    }

    private ExpectedAgent? Find(string repoHash, string agentId) =>
        _rows.FirstOrDefault(a =>
            string.Equals(a.RepoHash, repoHash, StringComparison.Ordinal) &&
            string.Equals(a.AgentId, agentId, StringComparison.Ordinal));

    private static ExpectedAgent Clone(ExpectedAgent a) => new()
    {
        Id = a.Id,
        RepoHash = a.RepoHash,
        AgentId = a.AgentId,
        Disposition = a.Disposition,
        DisposalReason = a.DisposalReason,
    };
}
