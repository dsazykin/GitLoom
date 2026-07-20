using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-14 test 4 (TI-P2-14.4) + the dual-mode rule (TI-P2-14.5). The coordinator's <c>spawn_worker</c> tool
/// is capped by limits/budgets/admission and never spawns directly (two-phase gate); manual-mode spawn
/// bypasses the coordinator but not admission/budgets.
/// </summary>
public class CoordinatorToolCapTests
{
    private static MemorySample UsedFraction(double used)
    {
        // total 100 KB, available = (1-used)*100 → UsedFraction == used.
        return new MemorySample(100, (long)Math.Round((1 - used) * 100));
    }

    private static TaskPlanFields Fields() => new(new[] { "src/a.cs" }, "approach", "tests");

    private sealed class FakeWorkerControl : IWorkerControl
    {
        public IReadOnlyList<string> ActiveWorkerIds { get; init; } = Array.Empty<string>();
        public Dictionary<string, string> Statuses { get; } = new();
        public string? WorkerStatus(string agentId) => Statuses.TryGetValue(agentId, out var s) ? s : null;
        public Task SendPromptAsync(string agentId, string prompt, CancellationToken ct) => Task.CompletedTask;
        public Task RequestVerificationAsync(string agentId, CancellationToken ct) => Task.CompletedTask;
    }

    // ---- Test 4 — SpawnCap_BudgetRejection ----

    [Fact]
    public void SpawnCap_BudgetRejection_AdmissionOverThreshold_RejectsWithoutDrafting()
    {
        var plans = new PlanApprovalService();
        var admission = new AdmissionController(sampler: () => UsedFraction(0.86)); // over the 85% ceiling
        var tools = new CoordinatorTools("coord-1", plans, admission, new FakeWorkerControl());

        var result = tools.SpawnWorker("Fix A", Fields(), "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Null(result.PlanId);
        Assert.Equal(0, plans.PendingCount("coord-1")); // no plan drafted, nothing spawned
    }

    [Fact]
    public void SpawnCap_BudgetRejection_BudgetExhausted_RejectsWithoutDrafting()
    {
        var plans = new PlanApprovalService();
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10)); // headroom fine
        var tools = new CoordinatorTools("coord-1", plans, admission, new FakeWorkerControl(),
            budgetExceeded: () => true);

        var result = tools.SpawnWorker("Fix A", Fields(), "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Equal(0, plans.PendingCount("coord-1"));
    }

    [Fact]
    public void SpawnCap_BudgetRejection_WorkerCapReached_RejectsWithoutDrafting()
    {
        var plans = new PlanApprovalService();
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var tools = new CoordinatorTools("coord-1", plans, admission, new FakeWorkerControl(),
            activeWorkerCount: () => 6, limits: new CoordinatorLimits(MaxActiveWorkers: 6));

        var result = tools.SpawnWorker("Fix A", Fields(), "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Equal(0, plans.PendingCount("coord-1"));
    }

    [Fact]
    public void SpawnWorker_WithinCaps_DraftsPendingPlan_ButNeverSpawnsDirectly()
    {
        var plans = new PlanApprovalService();
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var spawned = 0;
        plans.PlanApproved += _ => spawned++;
        var tools = new CoordinatorTools("coord-1", plans, admission, new FakeWorkerControl());

        var result = tools.SpawnWorker("Fix A", Fields(), "prompt", 1m);

        Assert.Equal(CoordinatorToolStatus.Ok, result.Status);
        Assert.NotNull(result.PlanId);
        Assert.Equal(1, plans.PendingCount("coord-1")); // pending — awaiting human approval
        Assert.Equal(0, spawned);                        // NOT spawned until approved
    }

    [Fact]
    public void SpawnWorker_S8PendingCapHit_ReturnsResourceExhausted()
    {
        var plans = new PlanApprovalService(options: new PlanApprovalOptions(MaxPendingPerCoordinator: 1, MaxDraftsPerWindow: 100));
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var tools = new CoordinatorTools("coord-1", plans, admission, new FakeWorkerControl());

        Assert.Equal(CoordinatorToolStatus.Ok, tools.SpawnWorker("A", Fields(), "p", 1m).Status);
        var second = tools.SpawnWorker("B", Fields(), "p", 1m);
        Assert.Equal(CoordinatorToolStatus.ResourceExhausted, second.Status);
    }

    [Fact]
    public void SpawnWorker_WhileFrozen_Rejected()
    {
        var plans = new PlanApprovalService();
        var admission = new AdmissionController(sampler: () => UsedFraction(0.10));
        var gate = new KillSwitchGate();
        gate.Freeze();
        var tools = new CoordinatorTools("coord-1", plans, admission, new FakeWorkerControl(), killGate: gate);

        var result = tools.SpawnWorker("A", Fields(), "p", 1m);
        Assert.Equal(CoordinatorToolStatus.Rejected, result.Status);
        Assert.Equal(0, plans.PendingCount("coord-1"));
    }

    // ---- TI-P2-14.5 — ManualModeSpawn_ShouldBypassCoordinator_ButNotAdmissionOrBudgets ----

    [Fact]
    public void ManualModeSpawn_ShouldBypassCoordinator_ButNotAdmissionOrBudgets()
    {
        // Manual mode does not go through the coordinator's plan gate, but shares the SAME admission gate,
        // so a manual spawn is refused for the same memory-pressure reason a coordinated spawn would be.
        var admission = new AdmissionController(sampler: () => UsedFraction(0.90), runningAgentCount: () => 3);

        Assert.False(admission.CanSpawn(out var reason));
        Assert.Contains("free memory or stop an agent", reason);

        // With headroom, admission permits it (coordinator not involved either way).
        var admissionOk = new AdmissionController(sampler: () => UsedFraction(0.10));
        Assert.True(admissionOk.CanSpawn(out _));
    }
}
