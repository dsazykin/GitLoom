# P2-30 — Automations, Scheduling & Agent Fleets — Implementation Plan

**Task ID:** P2-30 · **Milestone:** M7.75 · **Priority:** P1-parity (Superset automations, Codex
Automations, Jules scheduled tasks; MergeLoom Agent Fleets).
**Depends on:** P2-14 (approval), P2-27 (ticket intake as a trigger).
**Branch:** implement on `feature/P2-30-automations-fleets` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — virtual-clock triggers, dedup, fleet caps, policy-gated auto-approve audit, scripted nightly run; no human step.
>
> **Source of truth:** §P2-30 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** every automated run is a normal **governed** task (plan → approval per
> policy → sandbox → verification → queue), enters the stale-invalidation queue + conflict radar,
> and `PolicyAutoApprove` is an explicit, audited org policy — coordination, not just caps.

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-30 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-30** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-30 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

All agent work is human-initiated. Competitors ship scheduled/event-triggered agents; MergeLoom
ships fleets with PR caps. This task adds the trigger layer on the daemon — cron and events —
that feeds the *existing* governed pipeline, plus the fleet construct (mandate, cadence, budget,
caps, pause/resume).

### What you can rely on

| Fact | Where |
|---|---|
| Plan → approval → spawn pipeline (`PlanApprovalService`) | P2-14 |
| Ticket intake + routing rules (label triggers) | P2-27 |
| Budgets + admission (`IAiGateway`, `AdmissionController`) | P2-08 |
| Queue + review states (open-review counting) | P2-10 |
| Audit events (mandate changes etc.) | P2-15 |
| CI failure visibility (checks services, T-26) | `GitLoom.Core/Services/CheckStatusService.cs` |
| Repo events (`RepositoryChanged`, auto-fetch) | `GitLoom.Core/Services/RepositoryWatcher.cs`, `AutoFetchService.cs` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/AutomationService.cs` (`Automation` record, CRUD, run history) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/AutomationScheduler.cs` (cron evaluation; injectable clock) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/AutomationTriggers.cs` (event listeners: RepoEvent, CiFailure, TicketLabel → trigger firings with dedup) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/AgentFleet.cs` (mandate, cadence, budget ref, run cap, open-review cap, pause state) |
| **Create** | `GitLoom.App/ViewModels/Automations/AutomationsViewModel.cs`, `FleetViewModel.cs` + views (list/editor/history) |
| **Edit** | protos (automation CRUD + run history + fleet state) |
| **Create** | `GitLoom.Tests/AutomationSchedulerTests.cs`, `TriggerDedupTests.cs`, `FleetCapTests.cs`, `AutoApprovePolicyTests.cs`, `GitLoom.Tests/Integration/NightlyRunEndToEndTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// daemon GitLoom.Core/Agents/Orchestrator/AutomationService.cs
public sealed record Automation(string Id, string Name, AutomationTrigger Trigger /* Cron | RepoEvent | CiFailure | TicketLabel */,
    string TaskTemplate /* prompt/plan template */, ApprovalMode Approval /* AlwaysAsk | PolicyAutoApprove */);
```

Every run is a normal governed task: plan → (auto-)approval per org policy → sandbox →
verification → queue.

**Agent Fleets:** an automation may be a named fleet with a **mandate** (prompt template + path
scope + rules), **cadence**, **budget** (P2-08), **daily PR-producing-run cap**, and an
**open-review cap** (fleet pauses while ≥ N of its branches sit unreviewed); pause/resume per
fleet; mandate/scope/budget changes are audit events.

---

## 3. Implementation steps

1. **Triggers:**
   - `Cron`: standard 5-field cron parsing (small vetted parser or minimal in-house; injectable
     clock; misfire policy = run-once-on-recovery if within grace, else skip+log).
   - `RepoEvent`: main moved / new commits fetched (auto-fetch hook).
   - `CiFailure`: T-26 check status transitions to failing on the default branch.
   - `TicketLabel`: P2-27 routing-rule match on poll (label added → intake automation).
   All firings pass a **dedup key** (`automationId + cause fingerprint`, e.g. failing check run
   id, ticket id) — a trigger storm of N CI failures collapses per cause and then meets admission
   control (edge row 1).
2. **Run pipeline:** firing → instantiate `TaskTemplate` (prompt/plan template + trigger context)
   → `DraftPlan`-shaped structured plan → approval: `AlwaysAsk` (pending approval like any P2-14
   plan) or `PolicyAutoApprove` — allowed **only** when the org policy (P2-23 doc, or local
   explicit setting) enables it; every auto-approval emits a `plan_approved` audit event with
   `approver = policy:<id>`. Then the ordinary spawn → verify → queue path.
3. **Fleets:** fleet = automation + mandate + caps. Enforcement points: before spawn — daily
   PR-producing-run counter (runs whose branch reached `AwaitingReview`/PR) under cap;
   open-review counter (fleet branches in `AwaitingReview`) under cap, else fleet auto-pauses
   (loud state, resume manual or on review-drain). Budget = a P2-08 budget scoped to the fleet id.
   Mandate/scope/budget edits → audit events (`fleet_changed`).
4. **Editing semantics:** editing an automation while a run is live → live run unaffected;
   changes apply from the next firing (edge row 2). Disabled automation retains history (edge
   row 3). Run history rows: firing cause, plan, approval mode/identity, outcome links.
5. **UI:** automations list (trigger chips, last/next run, enabled toggle), editor (template with
   placeholder insertion, trigger config, approval mode with policy warning), fleet panel
   (mandate, caps with live counters, pause/resume), history.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| trigger storm (N CI failures) | dedup per cause + admission control; no spawn flood |
| automation edited while a run is live | live run unaffected; next-run semantics |
| disabled automation | retains history; never fires |
| open-review cap reached | fleet pauses loudly; resumes on drain or manual resume |
| auto-approve without policy enablement | typed refusal; run parked as pending approval |
| cron misfire (daemon down at fire time) | grace-window run-once or skip+log; never a burst of catch-up runs |

---

## 5. Invariants (MUST)

1. Every automated run is a governed task — no bypass of plan/approval/sandbox/verification/queue.
2. `PolicyAutoApprove` requires explicit, audited policy; auto-approvals are audit events.
3. Fleet caps enforced daemon-side before spawn.
4. Scheduler uses an injectable clock (deterministic tests).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Cron_ParseAndFire` | expression matrix → next-fire times (virtual clock); misfire grace behavior |
| 2 | `Trigger_DedupPerCause` | 5 failures same check run → 1 firing; distinct causes → distinct firings capped by admission |
| 3 | `AutoApprove_PolicyGated` | policy off → parked pending; policy on → spawned with `approver=policy:*` audit event |
| 4 | `Fleet_RunCapAndReviewCap` | counters enforce caps; cap hit → pause + loud state; drain → resumable |
| 5 | `Fleet_ChangesAudited` | mandate/scope/budget edits → `fleet_changed` events |
| 6 | `Edit_NextRunSemantics` | template edit mid-run → live run's plan unchanged; next firing uses new template |
| 7 | `NightlyRun_EndToEnd` (scripted) | cron fires → plan → policy auto-approve → worker → verified → queue entry; history row complete |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** any automated path skipping approval/verification; auto-approve as a silent
default; caps enforced only in UI; wall-clock reads in the scheduler.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Automation|FullyQualifiedName~Trigger|FullyQualifiedName~Fleet"
grep -rn "DateTime.UtcNow\|DateTime.Now" GitLoom.Core/Agents/Orchestrator/AutomationScheduler.cs   # 0 hits
```

---

## 8. Definition of done

- [ ] Four trigger types with dedup + admission; injectable-clock scheduler.
- [ ] Governed run pipeline with audited `PolicyAutoApprove`; run history.
- [ ] Fleets: mandate/cadence/budget/run-cap/open-review-cap, pause/resume, audited changes.
- [ ] Nightly end-to-end green; all edge rows covered.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-30**, base `phase2`.
