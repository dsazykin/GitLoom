# P2-31 — Dispatcher & Multi-Candidate Runs — Implementation Plan

**Task ID:** P2-31 · **Milestone:** M7.75 · **Priority:** P1-parity (Conductor Dispatcher, Cursor
multi-model).
**Depends on:** P2-08 (admission/budgets), P2-14 (approved plans), P2-29 (comparison view).
**Branch:** implement on `feature/P2-31-dispatcher-multi-candidate` off `phase2`; PR targets
`phase2`.

> **Source of truth:** §P2-31 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat:** candidates compared on **verification outcome + spend + diff risk score** (P2-11),
> not eyeballed diffs.

---

## 0. Context — what exists today

Spawning picks one adapter implicitly. Competitors route tasks to a chosen agent/model and run
multiple candidates. This task adds the routing layer (org defaults + adapter health/capability
metadata + simple telemetry heuristics — **no ML**) and the one-plan→N-workers fan-out that lands
in the P2-29 comparison view.

### What you can rely on

| Fact | Where |
|---|---|
| Adapter channel manifest (cli → version, health probe, capabilities extendable) | P2-22 `AdapterChannel` |
| Spawn path (plan-approved) + admission/budget checks | P2-14/P2-08 |
| Comparison view + winner-pick → rejection path | P2-29 |
| Risk classifier (diff risk score = flag-worthy hunk counts/ranks) | P2-11 |
| Run history / outcome telemetry (verification pass rate per adapter) | P2-10 records + P2-30 history |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/Dispatcher.cs` (`IDispatcher`: plan/task template → adapter+model choice; routing table + "auto") |
| **Create** | `GitLoom.Core/Agents/Orchestrator/AdapterCapabilities.cs` (per-adapter metadata: models, health, past success stats) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/MultiCandidateRun.cs` (fan-out record: planId → candidate agentIds; lifecycle) |
| **Edit** | spawn RPC/flow — accepts dispatcher choice or explicit selection; multi-candidate spawn |
| **Create** | `GitLoom.App/ViewModels/Agents/DispatchPickerViewModel.cs` (agent/model picker with health chips; "auto" default per org) + view wiring in the spawn/approval flow |
| **Create** | `GitLoom.Tests/DispatcherRoutingTests.cs`, `MultiCandidateSpawnTests.cs`, `CandidateComparisonHandoffTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **Dispatcher:** per-task agent/model selection with org defaults + per-CLI health/capability
  metadata from the adapter channel (P2-22); **"auto"** routes by task template + past success
  telemetry — simple heuristics first, **no ML**.
- **Multi-candidate:** one approved plan → N workers (different CLI/model each) in parallel
  (admission-capped); results land in the P2-29 comparison view; one winner merges, others
  reject.

---

## 3. Implementation steps

1. **Capabilities:** extend the adapter manifest consumption: per adapter — supported models,
   health-probe freshness, and rolling success stats computed from history (verification pass
   rate, mean repair attempts, mean spend per verified run; computed from P2-10/P2-08 records,
   cached). No new persistent store beyond a stats cache table.
2. **Routing table:** ordered rules `(taskTemplateTag | pathScope pattern | default) →
   (adapter, model)` configured per org/repo; `"auto"` = filter healthy adapters → rank by
   success-rate-then-cost for the matching template tag → pick top (deterministic tie-break by
   name). Pure function over inputs (table, capabilities, stats) — fully unit-testable.
3. **Explicit pick:** the spawn/approval UI gains the dispatch picker (adapter/model dropdowns,
   health chips, "auto" default per org policy). The choice is recorded on the plan/spawn
   (provenance: candidate metadata shows which CLI/model produced the branch).
4. **Multi-candidate fan-out:** on an approved plan, "Run N candidates" (2–3) → N spawns, each a
   distinct `(adapter, model)` from the dispatcher's top-N, same plan, admission-capped (spawn
   fewer than requested when admission blocks — surfaced, not silent). `MultiCandidateRun`
   groups them; each candidate is an ordinary queue entry.
5. **Comparison hand-off:** when ≥2 candidates reach `Verified`/`AwaitingReview` (or all reach a
   terminal-ish state), surface "Compare candidates" → P2-29 comparison preloaded with the
   group + the **diff risk score** per candidate (count/rank summary from `RiskClassifier` over
   each merge diff). Winner merges normally; losers → rejection path (P2-29 flow).
6. **Budgets:** candidate spawns draw from the task's budget; the fan-out multiplies estimated
   cost — show the multiplier in the confirm dialog.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| "auto" with an unhealthy top adapter | next healthy candidate chosen; unhealthy never picked |
| admission allows only 2 of 3 requested candidates | 2 spawn, shortfall surfaced |
| all candidates fail verification | group state `AllFailed`; comparison still available (logs/diffs) |
| winner picked while a loser still `Verifying` | verification cancelled → rejected (P2-29 row reused) |
| no telemetry yet (cold start) | "auto" falls back to org default order — deterministic |
| same adapter requested twice in one group | rejected — candidates must differ by (adapter, model) |

---

## 5. Invariants (MUST)

1. Routing is pure/deterministic given (table, capabilities, stats) — no ML, no randomness.
2. Every candidate is an ordinary governed queue entry (no shortcuts around verification).
3. Fan-out respects admission + budget; the group never over-spawns.
4. Candidate provenance records the (adapter, model) that produced each branch.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Routing_TableMatrix` | rules precedence (template tag > path scope > default); auto ranking by success/cost; cold-start fallback; deterministic ties |
| 2 | `Routing_HealthFilter` | unhealthy adapter skipped |
| 3 | `MultiCandidate_SpawnRespectsAdmission` | request 3, admission 2 → 2 spawned + shortfall reason |
| 4 | `MultiCandidate_DistinctPairsEnforced` | duplicate (adapter,model) → typed rejection |
| 5 | `Comparison_HandoffPayload` | group → P2-29 preload includes verification, spend, and risk-score summary per candidate |
| 6 | `Group_AllFailedState` | all fail → group state + comparison reachable |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** ML/random routing; candidates bypassing verification; fan-out ignoring admission;
a second stats pipeline (derive from existing records).

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Dispatcher|FullyQualifiedName~MultiCandidate"
grep -rn "Random\b" GitLoom.Core/Agents/Orchestrator/Dispatcher.cs   # 0 hits
```

---

## 8. Definition of done

- [ ] Capabilities + rolling success stats from existing records; pure deterministic router with "auto".
- [ ] Dispatch picker in the spawn/approval flow; choice recorded in provenance.
- [ ] Multi-candidate fan-out (admission/budget-capped, distinct pairs) → P2-29 comparison with verification+spend+risk.
- [ ] All edge rows tested. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-31**, base `phase2`.
