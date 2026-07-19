using System;
using System.IO;
using System.Linq;
using GitLoom.Core.Agents.Orchestrator;
using Mainguard.Git.Audit;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-14 plan-approval governance (tests 2, 3, 12 + the S-8 pressure signal). Approver identity is
/// daemon-derived and persisted; a rejected plan never spawns; the pending cap defends the human gate.
/// </summary>
public class PlanApprovalTests
{
    private static TaskPlanFields Fields() => new(new[] { "src/a.cs" }, "do the thing", "tests green");

    // ---- Test 2 — PlanRejected_NoResidue ----

    [Fact]
    public void PlanRejected_NoResidue()
    {
        var audit = new InMemoryAuditLog();
        var svc = new PlanApprovalService(audit: audit);

        // A spy spawner that would create a "worktree" dir if it ever ran (it must not, on reject).
        var worktreeRoot = Path.Combine(Path.GetTempPath(), "gitloom-plan-noresidue", Guid.NewGuid().ToString("N"));
        var spawnCount = 0;
        svc.PlanApproved += plan =>
        {
            spawnCount++;
            Directory.CreateDirectory(Path.Combine(worktreeRoot, plan.PlanId));
        };

        var draft = svc.Draft("coord-1", "Fix A", Fields(), "prompt", 1.5m);
        Assert.True(draft.IsDrafted);

        svc.Reject(draft.PlanId!, "not this way");

        // No worker ever spawned; no worktree residue.
        Assert.Equal(0, spawnCount);
        Assert.False(Directory.Exists(worktreeRoot));
        Assert.Equal(PlanStatus.Rejected, svc.Get(draft.PlanId!)!.Status);

        // Audit records the rejection, never an approval.
        var types = audit.Read().Select(e => e.Type).ToArray();
        Assert.Contains("plan_rejected", types);
        Assert.DoesNotContain("plan_approved", types);
    }

    // ---- Test 3 — Approval_PersistsIdentity (survives restart) ----

    [Fact]
    public void Approval_PersistsIdentity_SurvivesRestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gitloom-plan-persist", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var storePath = Path.Combine(dir, "plans.json");

        string planId;
        try
        {
            // First daemon instance: draft + approve with a DAEMON-DERIVED identity (never client-supplied).
            var svc1 = new PlanApprovalService(store: new JsonPlanApprovalStore(storePath));
            var draft = svc1.Draft("coord-1", "Refactor token refresh", Fields(), "prompt", 1.5m);
            planId = draft.PlanId!;
            var approved = svc1.Approve(planId, "uid:1000");
            Assert.Equal("uid:1000", approved.ApproverIdentity);
            Assert.NotNull(approved.DecidedAt);

            // Second daemon instance over the SAME store (a restart): the record + identity survive.
            var svc2 = new PlanApprovalService(store: new JsonPlanApprovalStore(storePath));
            var reloaded = svc2.Get(planId);
            Assert.NotNull(reloaded);
            Assert.Equal(PlanStatus.Approved, reloaded!.Status);
            Assert.Equal("uid:1000", reloaded.ApproverIdentity);
            Assert.Equal("Refactor token refresh", reloaded.Title);
            Assert.Equal(new[] { "src/a.cs" }, reloaded.Plan.Scope);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* cleanup only */ }
        }
    }

    [Fact]
    public void Approval_RequiresNonEmptyDaemonIdentity()
    {
        var svc = new PlanApprovalService();
        var draft = svc.Draft("coord-1", "X", Fields(), "p", 1m);
        // An empty approver would be an unattributable approval — refused.
        Assert.Throws<ArgumentException>(() => svc.Approve(draft.PlanId!, ""));
    }

    // ---- Test 12 — PendingPlanCap_ExcessDraftsRejected (S-8) ----

    [Fact]
    public void PendingPlanCap_ExcessDraftsRejected()
    {
        var audit = new InMemoryAuditLog();
        var svc = new PlanApprovalService(
            audit: audit,
            options: new PlanApprovalOptions(MaxPendingPerCoordinator: 3, MaxDraftsPerWindow: 100));

        for (var i = 0; i < 3; i++)
        {
            Assert.True(svc.Draft("coord-1", $"Task {i}", Fields(), "p", 1m).IsDrafted);
        }

        // The N+1th concurrent PlanPending draft is refused (RESOURCE_EXHAUSTED) + audited.
        var excess = svc.Draft("coord-1", "Task overflow", Fields(), "p", 1m);
        Assert.Equal(DraftOutcome.ResourceExhausted, excess.Outcome);
        Assert.Null(excess.PlanId);
        Assert.Equal(3, svc.PendingCount("coord-1"));

        var rejects = audit.Read().Where(e => e.Type == "plan_draft_rejected").ToArray();
        Assert.Single(rejects);
        Assert.Equal("pending-cap", rejects[0].Fields["cause"]);

        // The pressure signal reflects the N pending plans.
        var pressure = svc.PressureSignal("coord-1");
        Assert.NotNull(pressure);
        Assert.Contains("3 plans pending", pressure);
    }

    [Fact]
    public void PendingPlanCap_DecidingAPlan_FreesCapacity()
    {
        var svc = new PlanApprovalService(options: new PlanApprovalOptions(MaxPendingPerCoordinator: 2, MaxDraftsPerWindow: 100));
        var d1 = svc.Draft("coord-1", "A", Fields(), "p", 1m);
        svc.Draft("coord-1", "B", Fields(), "p", 1m);
        Assert.Equal(DraftOutcome.ResourceExhausted, svc.Draft("coord-1", "C", Fields(), "p", 1m).Outcome);

        // Approving one pending plan frees a slot (approved plans are no longer PlanPending — S-8).
        svc.Approve(d1.PlanId!, "uid:1000");
        Assert.True(svc.Draft("coord-1", "C", Fields(), "p", 1m).IsDrafted);
    }
}
