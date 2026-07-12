# P2-42 — Merge-Train Simulation, Verification Cache & Test-Impact Ordering — Implementation Plan

**Task ID:** P2-42 · **Milestone:** M7.75 · **Priority:** P1 differentiator (novel — extends
P2-10).
**Depends on:** P2-10, P2-19.
**Branch:** implement on `feature/P2-42-merge-train-simulation` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — train fixtures with induced transitive conflicts, cache key matrix, impact-subset selection, receipts chain; no human step.
>
> **Source of truth:** §P2-42 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Positioning:** competitors verify branch-by-branch against main; nobody shows "what main
> looks like after all five land."

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-42 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-42** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-42 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-10 verifies each branch against current main and invalidates on movement; P2-19 warns on
pairwise overlap; P2-35 detects flakes. Three extensions make the queue feel instant and
predictive: whole-queue dry-runs, content-addressed verification caching with signed receipts,
and impacted-tests-first ordering.

### What you can rely on

| Fact | Where |
|---|---|
| Queue order + states + verification runner (sandbox exec) | P2-10 |
| Scratch-capable worktree manager (bare mirror; agent worktrees are sacred — simulation gets its own) | P2-06 |
| Pairwise conflict classification (chunker) | P2-19/T-02 |
| Flake detection | P2-35 |
| Audit chain (receipts) | P2-15 |
| Test log parsing (TRX/xUnit) for per-test results | P2-11 test-delta parser |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/MergeTrainSimulator.cs` (scratch worktree, sequential rebase+merge dry-run, conflict report, combined verification) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/VerificationCache.cs` (content-addressed store keyed `(merge-base SHA, branch tree hash, test-command hash)`; receipt linkage) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/VerificationReceipt.cs` (signed receipt records → P2-15 chain) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/TestImpactMap.cs` (test↔file coverage accumulation; impacted-subset selection) |
| **Edit** | `MergeQueue` — cache consult before running; preliminary (impacted) vs full runs; train state exposure |
| **Create** | `GitLoom.App/ViewModels/Agents/MergeTrainViewModel.cs` + view (train on the queue panel, per-car status) |
| **Create** | `GitLoom.Tests/MergeTrainTests.cs`, `VerificationCacheTests.cs`, `TestImpactTests.cs`, `ReceiptChainTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

1. **Merge-train simulation ("pre-flight"):** dry-run the whole queue in order in a **scratch
   worktree** — sequential rebase+merge of all queued branches → pairwise/transitive conflict
   report + **one combined verification run**. UI: a train view on the queue panel with per-car
   status.
2. **Verification cache & receipts:** results content-addressed by `(merge-base SHA, branch tree
   hash, test-command hash)` — re-queues after unrelated merges hit cache instead of re-running;
   each pass/fail is a **signed receipt chained in P2-15**.
3. **Test-impact ordering:** a test↔file coverage map accumulated from prior runs; queue entries
   run the impacted subset first for a fast preliminary verdict, **full suite before merge**.

---

## 3. Implementation steps

1. **Simulator:** scratch worktree from the bare mirror (`~/gitloom/worktrees/<repo>/__train__`,
   recreated per simulation — never an agent worktree, never main; invariant 1). Sequence: reset
   to main → for each queued branch in order: `merge --no-ff` (or rebase-then-merge mirroring the
   real path) → record per-car outcome (clean / conflict with files+pair attribution via the
   chunker for "which earlier car caused it") → on full success run **one combined verification**
   in a simulation sandbox → train report `(cars[], combinedVerification?)`. Human merge lands
   mid-simulation → abort + re-simulate (edge row 1); debounce re-simulations.
2. **Cache:** key = `(mergeBaseSha, branchTreeHash /* git rev-parse <branch>^{tree} */,
   testCommandHash)`. On `RunVerificationAsync`: consult first — hit ⇒ **no run**, but a new
   receipt referencing the original run's receipt (invariant: cache hits still record — the
   ledger stays complete). Miss ⇒ run, store, receipt. Flaky-marked results (P2-35) are
   **non-cacheable** (edge row 2). Eviction: LRU cap + retention window.
3. **Receipts:** `VerificationReceipt { key, passed, logRef, originalReceiptId?, when }` — signed
   (daemon key; P2-43's identity infrastructure when present, else the daemon session key) and
   appended to the P2-15 chain (`verification_receipt` events). `audit replay` (P2-15) picks
   these up automatically.
4. **Test-impact map:** parse per-test results + the diff of each verified run → accumulate
   `test → touched-files` co-occurrence (heuristic, monotonically improving; stored per repo).
   Selection: entry's changed files → impacted tests (map hits + always-run smoke set) →
   **preliminary verdict** run first (fast feedback in the queue UI); the **full suite still
   runs before merge** — preliminary never gates (invariant 3). Cold start ⇒ full-suite only
   (edge row 3).
5. **UI:** train strip on the queue panel — cars in order with status chips (clean / conflict
   (with cause) / verified-combined); preliminary vs full verdict badges on entries; cache-hit
   indicator ("verified via cache → receipt").

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| train invalidated mid-simulation by a human merge | aborted + re-simulated |
| cache poisoned by a flaky test | flake detection marks the receipt non-cacheable |
| impact map cold-start | full suite until warm |
| conflict at car 3 of 5 | cars 4–5 evaluated against the 1+2 state (transitive attribution recorded) |
| combined verification fails but individual runs passed | train reports the combination failure distinctly |

---

## 5. Invariants (MUST)

1. Simulation happens in scratch worktrees only — never agent worktrees or main.
2. Cache hits still record a receipt (referencing the original).
3. Preliminary verdicts never gate a merge — only full runs do.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Train_FiveBranchFixture` | induced transitive conflict → per-car report with cause attribution; clean queue → combined verification runs once |
| 2 | `Train_InvalidatedByMerge` | main move mid-sim → abort + re-simulate |
| 3 | `Cache_HitMissFlakeMatrix` | identical key → hit + referencing receipt, no run (spy); tree change → miss; flaky → never cached |
| 4 | `Receipts_Chained` | receipts land in the audit chain; `VerifyAll` green |
| 5 | `Impact_SubsetSelection` | fixture map + changed files → expected test subset + smoke set; cold start → full |
| 6 | `Preliminary_NeverGates` | preliminary pass + full not run → `CanMerge` false |
| 7 | `Simulator_ScratchOnly` | worktree paths used == `__train__` only (spy) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** simulation touching agent worktrees/main; cache hits without receipts;
preliminary verdicts gating merges; caching flaky results.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~MergeTrain|FullyQualifiedName~VerificationCache|FullyQualifiedName~TestImpact|FullyQualifiedName~ReceiptChain"
grep -rn "__train__" GitLoom.Core/Agents/Orchestrator/MergeTrainSimulator.cs   # scratch namespace present
```

---

## 8. Definition of done

- [ ] Whole-queue dry-run in scratch worktrees with per-car/transitive conflict attribution + combined verification; train UI.
- [ ] Content-addressed cache with signed, chained receipts (hits reference originals; flakes excluded).
- [ ] Impact map + impacted-first preliminary runs; full suite remains the only merge gate.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-42**, base `phase2`.
