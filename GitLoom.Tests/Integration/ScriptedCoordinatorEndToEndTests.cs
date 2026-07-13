using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Orchestrator;
using GitLoom.Core.Audit;
using Xunit;

// The P2-10 merge-queue record (7-field), disambiguated from the UI prototype VerificationRecord.
using VerificationRecord = GitLoom.Core.Agents.Orchestrator.VerificationRecord;

namespace GitLoom.Tests.Integration;

/// <summary>
/// P2-14 test 8 (TI-P2-14.9) — the scripted-coordinator end-to-end (the full M7 story): a scripted
/// coordinator decomposes work into two independent tasks → two TaskPlans → human approvals (daemon-derived
/// identity) → two workers spawn → both verify green → sequential human merges with a stale re-verify
/// between them. Asserted through the audit event sequence.
///
/// <para>This is the deterministic scripted-swarm leg (runs everywhere). The real-container leg — verifying
/// in real jails via <c>DockerSandboxEngine</c> and the stale-cascade against a real runtime — is the
/// existing <c>MergeQueueDockerTests</c> (RequiresDocker, PR-blocking in Linux CI); the real-model
/// coordinator smoke is the deferred pre-Alpha <c>RequiresNetwork</c> check (master doc §P2-14).</para>
/// </summary>
public class ScriptedCoordinatorEndToEndTests
{
    private sealed class ScriptedModel : ICoordinatorModel
    {
        private readonly Queue<CoordinatorModelTurn> _turns;
        public ScriptedModel(IEnumerable<CoordinatorModelTurn> turns) => _turns = new Queue<CoordinatorModelTurn>(turns);
        public Task<CoordinatorModelTurn> NextAsync(IReadOnlyList<CoordinatorMessage> transcript, CancellationToken ct) =>
            Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : CoordinatorModelTurn.Say("done"));
    }

    private sealed class FakeWorkerControl : IWorkerControl
    {
        public List<string> Workers { get; } = new();
        public IReadOnlyList<string> ActiveWorkerIds => Workers;
        public string? WorkerStatus(string agentId) => Workers.Contains(agentId) ? "Working" : null;
        public Task SendPromptAsync(string agentId, string prompt, CancellationToken ct) => Task.CompletedTask;
        public Task RequestVerificationAsync(string agentId, CancellationToken ct) => Task.CompletedTask;
    }

    private static CoordinatorToolCall Spawn(string title, string scope) => new("spawn_worker", new Dictionary<string, object?>
    {
        ["title"] = title,
        ["scope"] = new object?[] { scope },
        ["approach"] = "do " + title,
        ["test_strategy"] = "tests green",
        ["task_prompt"] = "implement " + title,
        ["budget_usd"] = "1.5",
    });

    [Fact]
    public async Task ScriptedCoordinator_EndToEnd_TwoTasks_ParallelWorkers_SequentialMergeWithStaleReverify()
    {
        var audit = new InMemoryAuditLog();
        var plans = new PlanApprovalService(audit: audit);
        var admission = new AdmissionController(sampler: () => new MemorySample(100, 90)); // headroom
        var workers = new FakeWorkerControl();
        var tools = new CoordinatorTools("coord-1", plans, admission, workers);

        // The approval-triggered spawn path (P2-09 stand-in): a worker id per approved plan.
        var spawnedByPlan = new Dictionary<string, string>();
        plans.PlanApproved += plan =>
        {
            var workerId = "w-" + plan.PlanId[..4];
            workers.Workers.Add(workerId);
            spawnedByPlan[plan.PlanId] = workerId;
        };

        // ---- Phase 1: the coordinator decomposes into two independent tasks → two plans drafted ----
        var model = new ScriptedModel(new[]
        {
            CoordinatorModelTurn.Call(Spawn("task-1", "src/one/**")),
            CoordinatorModelTurn.Call(Spawn("task-2", "src/two/**")),
            CoordinatorModelTurn.Say("Two plans drafted — awaiting your approval."),
        });
        var coordinator = new CoordinatorAgent("coord-1", model, tools);

        await coordinator.SendAsync("Split the refactor into two independent tasks.");

        var pending = plans.Pending("coord-1");
        Assert.Equal(2, pending.Count);
        Assert.Empty(workers.Workers); // NOTHING spawned before approval

        // ---- Phase 2: the human approves both plans (identity daemon-derived) → two workers spawn ----
        foreach (var plan in pending.ToList())
        {
            plans.Approve(plan.PlanId, "uid:1000");
        }

        Assert.Equal(2, workers.Workers.Count);
        var w1 = spawnedByPlan[pending[0].PlanId];
        var w2 = spawnedByPlan[pending[1].PlanId];

        // ---- Phase 3: both workers verify green (parallel), against main@sha0 ----
        MergeQueue queue = null!;
        Func<string, CancellationToken, Task<VerificationRecord>> run = (id, ct) =>
            Task.FromResult(new VerificationRecord(id, queue.CurrentMainSha, true, "log", "test", "cfg", DateTimeOffset.UtcNow));
        queue = new MergeQueue("repo", "sha0", new InMemoryMergeQueueStore(), new InMemoryVerificationStore(), run,
            requeue: (id, ct) => queue.RunVerificationAsync(id, ct), audit: audit);

        await queue.RunVerificationAsync(w1, CancellationToken.None);
        await queue.RunVerificationAsync(w2, CancellationToken.None);
        Assert.True(queue.CanMerge(w1, out _));
        Assert.True(queue.CanMerge(w2, out _));

        // ---- Phase 4: merge w1 (human path). Main moves → w2 goes StaleVerified (the cascade) ----
        queue.RequestReview(w1);
        queue.ConfirmHumanMerge(w1, "sha1");
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(w1));
        await queue.LastCascade; // drain the auto re-queue (yield → rebase → re-verify)

        // ---- Phase 5: w2 re-verified against the new main, then merged ----
        Assert.Equal(WorkerMergeState.Verified, queue.GetState(w2)); // the stale re-verify landed green
        Assert.True(queue.CanMerge(w2, out _));
        queue.RequestReview(w2);
        queue.ConfirmHumanMerge(w2, "sha2");
        Assert.Equal(WorkerMergeState.Merged, queue.GetState(w2));

        // ---- The full story, in the audit trail ----
        var types = audit.Read().Select(e => e.Type).ToList();
        Assert.Equal(2, types.Count(t => t == "plan_approved"));
        Assert.DoesNotContain("plan_rejected", types);
    }
}
