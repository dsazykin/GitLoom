# P3-09 — AI CI/CD Janitor (Org-Scale, Governed) — Implementation Plan

**Task ID:** P3-09 · **Milestone:** M10+ (requires merge-gate reputation first) · **Priority:**
P2.
**Depends on:** P2-10 (queue), P2-12 (host-API merge path), P2-26 (breaker pattern), P2-30
(automation/trigger infrastructure).
**Branch:** implement on `feature/P3-09-cicd-janitor` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated scripted-CI-fixture chain (failure-plan-queue-PR) + **model testing on repair quality** before enabling auto-approve policies.
> Flake suppression, dedup, and the governed chain are deterministic. Real repair quality against a live CI failure set needs model runs + human grading before any org enables `janitor`-class auto-approve.
>
> **Source of truth:** §P3-09 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **The governed difference** from Composio AO / Jules auto-fix: the janitor proposes through
> the same verified, audited, human-gated pipeline — auto-approve is an explicit org policy with
> its own audit event, never the default.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P3-09 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P3-09** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P3-09 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-30 already ships the `CiFailure` automation trigger with dedup; P2-14 the plan gate; P2-10
the queue; P2-12 the host-PR ship path; P2-35 flake detection; P2-26 the circuit-breaker
pattern. The janitor is a composition: a CI-failure watcher scoped to main/release branches
that spawns **scoped** repair workers and ships fixes as PRs.

### What you can rely on

| Fact | Where |
|---|---|
| Check-status watching (T-26 seam) + `CiFailure` trigger + dedup | `CheckStatusService` / P2-30 `AutomationTriggers` |
| Two-phase plan approval + `PolicyAutoApprove` (audited) | P2-14/P2-30 |
| Queue + verification + repair loop + flake detection | P2-10/P2-35 |
| Host-API PR creation/merge path | P2-12 `MergeDispatch` / T-23 |
| Circuit-breaker math | P2-26 `CircuitBreaker` |
| Failure-log capture (check runs → logs via host API) | T-26 services |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/CiJanitor.cs` (watcher config: branches, checks scope; failure → task pipeline) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/FailureFingerprint.cs` (pure: check run + normalized log tail → hash; shared with dedup/flake) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/JanitorPlanDrafter.cs` (failure context → auto-generated `TaskPlan` scoped to the failing check's diff context) |
| **Edit** | P2-30 `AutomationService` — `janitor` automation class (approval mode wiring, `janitor` policy class) |
| **Edit** | P2-26 breaker reuse — per-fingerprint repair breaker (repeated failed repairs → escalate) |
| **Create** | `GitLoom.App/ViewModels/Automations/JanitorViewModel.cs` (config + run history + escalations) |
| **Create** | `GitLoom.Tests/CiJanitorTests.cs`, `FailureFingerprintTests.cs`, `JanitorFlakeSuppressionTests.cs`, `JanitorDedupTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

A daemon service that watches configured CI (host checks API — T-26 seam) for **failures on main
or release branches**, and on failure:

- spawns a repair worker (P2-14 two-phase — the plan is **auto-generated but still requires
  approval** unless the org policy enables auto-approve for the `janitor` class),
- **scoped to the failing check's diff context**,
- the fix branch enters the P2-10 queue like any agent branch,
- and ships as a PR via **P2-12's host-API merge path**.

---

## 3. Implementation steps

1. **Watcher:** per-repo janitor config: watched branches (default: default branch + `release/*`
   globs), watched checks (all or named), poll cadence riding the T-26 refresh. Failure detected
   → build the **failure fingerprint**: `(check name, normalized log tail hash)` — normalization
   reuses the P2-26 trace-normalizer rules (timestamps/paths/addresses stripped).
2. **Flake suppression first:** same fingerprint passes on a re-run (host re-run API where
   available, else next scheduled run) → mark flaky, **don't spawn** (edge row 1; reuse the
   P2-35 flake semantics).
3. **Dedup:** one live janitor task per fingerprint — a second failure event with the same hash
   attaches to the existing task (edge row 3).
4. **Plan drafting:** `JanitorPlanDrafter` — failure context (check name, log tail, the commit
   range since last green, the failing check's diff context = files touched in that range) →
   auto-generated `TaskPlan` (Scope = the range's files, Approach = repair framing, TestStrategy
   = the failing check). Through the gateway, schema-validated (P2-14). Approval: `AlwaysAsk`
   default; `PolicyAutoApprove` only via the org policy's `janitor` class — every auto-approval
   audited (`approver=policy:janitor`).
5. **Execution:** worker spawns in the ordinary sandbox scoped to a worktree at the failing
   branch tip; fix branch `agent/janitor-<fingerprint>` enters the P2-10 queue (verification =
   the failing check's command where runnable locally, else the configured test command); ships
   as a host PR via the P2-12 path (the janitor never pushes to main — the human/host gate
   stands).
6. **Repair breaker:** repeated failed repairs for one fingerprint (default 2 attempts across
   tasks) → breaker trips → escalation event + janitor pause for that fingerprint (P2-26
   pattern; edge row 2). Escalations render in the janitor panel + notifications.
7. **UI:** config (branches/checks/approval class), run history (fingerprint, plan, outcome,
   PR link), escalations, pause/resume — riding the P2-30 automation surfaces (a janitor is a
   specialized automation, not a parallel system).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| flaky test (same failure hash passes on re-run) | marked flaky, no worker spawned |
| repeated failed repairs | circuit breaker + escalation; fingerprint paused |
| two janitor workers for the same failure | dedup by failure hash — second attaches, never spawns |
| failure on an unwatched branch | ignored |
| auto-approve without the org policy | parked pending approval (P2-30 semantics) |
| fix PR goes stale (main moves) | ordinary P2-10 stale semantics on the queue entry |

---

## 5. Invariants (MUST)

1. The janitor proposes through the same verified, audited, human-gated pipeline — no direct
   pushes, no approval bypass.
2. Auto-approve is an explicit org policy with its own audit event.
3. Fingerprint logic is pure and shared (dedup, flake, breaker all key on it).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Fingerprint_NormalizationStability` | same failure, varying noise → same hash; different failures → different |
| 2 | `FailureEvent_ToQueue_Integration` | scripted CI fixture: failure → drafted plan → (approval) → worker → queue entry → host-PR dispatch (spies through the chain) |
| 3 | `Flake_Suppression` | re-run passes → flaky mark, zero spawns |
| 4 | `Dedup_SameHashAttaches` | two events, one task |
| 5 | `Breaker_EscalatesAndPauses` | two failed repair tasks → escalation event, fingerprint paused, resume manual |
| 6 | `AutoApprove_PolicyAuditTrail` | policy on → spawn with `approver=policy:janitor` audit event; off → pending |
| 7 | `UnwatchedBranch_Ignored` | failure on a feature branch → no action |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** direct pushes/merges by the janitor; auto-approve as default; a parallel
automation system beside P2-30; unbounded repair attempts.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~CiJanitor|FullyQualifiedName~FailureFingerprint|FullyQualifiedName~Janitor"
grep -rn "push origin main\|ForegroundMerge" GitLoom.Core/Agents/Orchestrator/CiJanitor.cs   # 0 hits
```

---

## 8. Definition of done

- [ ] Watcher (branch/check scoped) + pure shared fingerprinting; flake suppression + dedup.
- [ ] Auto-drafted plans through the standard approval gate (`janitor` policy class audited).
- [ ] Fix branches through queue → host-PR path; repair breaker + escalation + pause/resume.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P3-09**, base `phase2`.
