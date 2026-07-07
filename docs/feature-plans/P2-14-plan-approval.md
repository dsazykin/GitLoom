# P2-14 — Plan Approval + Dual-Mode Orchestration — Implementation Plan

**Task ID:** P2-14 · **Milestone:** M7 · **Priority:** P0 — the product thesis.
**Depends on:** P2-08 (budgets/admission), P2-09 (worker lifecycle), P2-13 (coordinator tab UI).
**Branch:** implement on `feature/P2-14-plan-approval` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-14 of `docs/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.5, with plan-approval promoted to the headline). Security-adjacent: the
> input-lock and role invariants are enforced **daemon-side**, never UI-side.

---

## 0. Context — what exists today

Everything below the orchestration layer exists after P2-08/09/13: spawn-capable sandboxes,
lifecycle, gateway, UI chrome. This task adds the two operating modes — **manual** (user drives
each agent directly) and **coordinated** (a Coordinator chat agent decomposes work and spawns
workers) — with the governance spine: structured plans approved by a human before any worker
starts, daemon-enforced terminal locking for managed workers, and one always-visible kill switch.

### What you can rely on

| Fact | Where |
|---|---|
| Agent spawn/stop RPCs + lifecycle + teardown | P2-02/P2-09 |
| `IAiGateway` leases + budgets; `AdmissionController.CanSpawn` | P2-08 |
| Coordinator pinned tab + `IsAttentionRequired` pulse + workspaces | P2-13 |
| Merge queue human gate (`AwaitingReview` → human merge only) | P2-10 |
| Adapter channel (agent CLIs at pinned versions) | P2-22 (parallel; use the P2-03 spawn shape meanwhile) |
| Audit journal rows for approvals (G-17; chained later by P2-15) | plain rows now |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/CoordinatorAgent.cs` (chat agent host: tool loop over the gateway) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/CoordinatorTools.cs` (`spawn_worker`, `get_worker_status`, `send_worker_prompt`, `request_verification` — tool schemas + dispatch) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/TaskPlan.cs` (`TaskPlan { Scope: files[], Approach, TestStrategy }` + JSON-schema validation) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/PlanApprovalService.cs` (pending plans, approve/reject, approver OS identity persisted) |
| **Create** | `GitLoom.Server/Auth/RoleInterceptor.cs` (coordinator credential class: merge RPCs denied; worker input RPCs denied for locked terminals) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/KillSwitch.cs` (yield-all → pause fan-out + queue freeze + journal snapshot) |
| **Create** | `GitLoom.App/ViewModels/Agents/CoordinatorChatViewModel.cs`, `PlanApprovalViewModel.cs`, kill-switch command surfaced in `ActivityBarViewModel` |
| **Create** | corresponding Views (`CoordinatorChatView`, `PlanApprovalView`) |
| **Create** | `GitLoom.Tests/TaskPlanSchemaTests.cs`, `PlanApprovalTests.cs`, `CoordinatorToolCapTests.cs`, `InputLockGrpcTests.cs`, `KillSwitchTests.cs`, `GitLoom.Tests/Integration/ScriptedCoordinatorEndToEndTests.cs` |
| **Edit** | protos (coordinator chat bridge, plan approval RPCs, kill switch RPC) + `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **`CoordinatorAgent`** — chat agent with **no code, no worktree, no merges**; tools:
  `spawn_worker(taskSpec)`, `get_worker_status`, `send_worker_prompt`, `request_verification`;
  every tool capped by limits/budgets/admission.
- **Two-phase spawn:** structured `TaskPlan { Scope: files[], Approach, TestStrategy }`
  (JSON-schema validated) → rendered for approval → **workers start only on approved plans**;
  plan + approver OS identity persisted (P2-15 chains it).
- **Terminal locking** for managed workers enforced **daemon-side** — the input stream is severed
  at the gRPC layer (interceptor rejects `Attach` input frames for locked agents), not just UI
  read-only.
- **Kill switch:** yield-all (timeout → `docker pause`) + queue freeze + journal snapshot; one
  always-visible control.
- **Human handoff:** `AwaitingReview` badge; merges only via the P2-10 human path — the
  coordinator **cannot invoke merge RPCs** (interceptor-enforced role, not convention).
- Coordinator serializes dependent tasks; partitioning quality (parallel vs serialized ratio,
  conflict-radar hits between its workers) is tracked telemetry.

---

## 3. Implementation steps

1. **`TaskPlan` + schema:** POCO + embedded JSON schema; `Validate(json) → errors[]`. Corpus of
   valid/invalid fixtures (missing scope, empty files, wrong types, extra fields tolerated?—
   decide: reject unknown top-level fields for forward-compat honesty).
2. **`PlanApprovalService`:** coordinator's `spawn_worker` lands a **pending plan** (persisted,
   with the coordinator's stated task spec); UI renders it (scope file list, approach, test
   strategy); approve records `(plan, approver OS identity — Environment.UserName + SID on
   Windows, uid on Linux, timestamp)` and only then does the spawn proceed (P2-09 path,
   admission + budget checks apply). Reject → nothing spawned, **no worktree residue** (edge
   row 1), coordinator informed via tool result.
3. **`CoordinatorAgent`:** system-prompted chat loop over the gateway (P2-08 lease per turn);
   tool dispatch through `CoordinatorTools` with hard caps (max workers, per-day budget,
   admission check before spawn tool returns). The coordinator's container (if any) gets **no
   worktree mount and no git credentials** — it is chat + tools only.
4. **Role enforcement:** daemon issues distinct credential classes: the coordinator's channel
   token carries role `coordinator` — `RoleInterceptor` denies merge RPCs and human-only RPCs to
   that role (test with a hand-crafted client); worker terminal locking: agents spawned in
   coordinated mode are `Locked` — `Attach` input frames rejected at the interceptor (read-only
   streams still flow); manual-mode agents unlocked.
5. **Kill switch:** fan-out yield-all with per-agent timeout → `docker pause`; freeze the merge
   queue (no state transitions except explicit human resume); journal snapshot (agent list +
   states + queue) written before returning; **< 5 s to all-frozen** (invariant, timed test with
   scripted containers). Always-visible control in the activity bar (P2-13 surface).
6. **Dual mode:** manual-mode spawn (user-initiated) bypasses the coordinator but **not**
   admission/budgets (edge row 3); mode is per-agent, coexisting.
7. **Telemetry:** counters for plans proposed/approved/rejected, workers serialized vs parallel,
   mid-flight conflicts (P2-19 feed when present).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| plan rejected | worker never spawns, no worktree residue |
| kill switch with an agent mid-yield | pause lands after the yield timeout; total < 5 s |
| manual-mode spawn | bypasses coordinator, still admission/budget-checked |
| plan JSON fails schema | typed rejection to the coordinator tool call; nothing persisted as pending |
| approve after daemon restart | pending plans persisted; approval path intact |

---

## 5. Invariants (MUST)

1. Input-lock verified **at the gRPC layer** by test — a hand-crafted client sending input frames
   to a locked agent is rejected.
2. Kill switch freezes all containers **< 5 s**.
3. The coordinator cannot invoke merge RPCs — interceptor-enforced role, not convention.
4. Workers start only on approved plans; approver identity persisted with the plan.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `TaskPlan_SchemaCorpus` | valid/invalid fixture matrix → exact error sets |
| 2 | `PlanRejected_NoResidue` | reject → zero worktrees/containers/branches for that task |
| 3 | `Approval_PersistsIdentity` | approve → plan + OS identity + timestamp row; survives restart |
| 4 | `SpawnCap_BudgetRejection` | coordinator exceeding caps → tool error, no spawn |
| 5 | `InputLock_GrpcLayer` | raw gRPC client sends input to locked agent → rejected; read stream still works |
| 6 | `RoleInterceptor_DeniesMergeToCoordinator` | coordinator-token call to merge RPC → `PERMISSION_DENIED` |
| 7 | `KillSwitch_FanOutUnder5s` | 3 scripted agents (one ignoring yield) → all paused/frozen < 5 s; journal snapshot written; queue frozen |
| 8 | `ScriptedCoordinator_EndToEnd` (`RequiresDocker`) | scripted coordinator: 2 independent tasks → 2 plans → approvals → parallel workers → verified → sequential human merges with a stale re-verify between (the full M7 story) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** UI-only input locking; coordinator with a worktree/credentials; spawn before
approval; kill switch that only signals without pausing; role checks by convention.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~TaskPlan|FullyQualifiedName~PlanApproval|FullyQualifiedName~CoordinatorTool|FullyQualifiedName~InputLock|FullyQualifiedName~KillSwitch"
grep -rn "IsReadOnly" GitLoom.App/ViewModels/Agents/ | grep -i terminal   # UI read-only may exist, but never alone — check RoleInterceptor coverage
```

---

## 8. Definition of done

- [ ] `TaskPlan` schema + approval service (identity persisted, restart-safe).
- [ ] Coordinator chat agent with capped tools, no code/worktree/merge power (interceptor-proven).
- [ ] Daemon-side terminal locking; kill switch < 5 s with snapshot; dual-mode spawn rules.
- [ ] Scripted end-to-end green incl. stale re-verify between merges.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-14**, base `phase2`.
