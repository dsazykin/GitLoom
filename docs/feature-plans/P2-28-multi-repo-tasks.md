# P2-28 — Multi-Repo Tasks + Epic Slices — Implementation Plan

**Task ID:** P2-28 · **Milestone:** M7.75 · **Priority:** P0-parity (Kepler's headline).
**Depends on:** P2-C3 (multi-repo dashboard), P2-06 (per-repo provisioning), P2-27 (epic import).
**Branch:** implement on `feature/P2-28-multi-repo-tasks` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-28 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** cross-repo verification gating (don't merge repo A's half of a contract
> change until repo B's half verified) + conflict radar across the task's repos + slice ordering
> from **measured overlap** (P2-19), not only declared dependencies.

---

## 0. Context — what exists today

Every orchestration concept so far is single-repo: one worktree, one queue entry per branch.
Kepler's headline is tasks spanning repos. This task adds an **orchestration record** — not a new
VCS concept — that fans one Task across N repos (one worker each), injects a shared context
document, gates merges on sibling verification, and supports dependency-ordered epic slices.

### What you can rely on

| Fact | Where |
|---|---|
| Per-repo provisioning + worktrees (`repoHash` keyed) | P2-06 |
| Queue states + `CanMerge` gate hooks (`IMergeGate`) | P2-10 |
| Plan approval; multi-task plan skeleton from epics | P2-14 / P2-27 `EpicImporter` |
| Registered repo set + overview cards | P2-C3 `WorkspaceOverviewService` |
| Conflict radar per repo (`Scan(repoHash)`) | P2-19 |
| Yield discipline (shared-context injection point) | P2-09 |
| Per-agent spend + gate telemetry | P2-08/P2-10 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/MultiRepoTask.cs` (record + `MultiRepoTaskService`: lifecycle, persistence) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/SharedContextDocument.cs` (task brief + cross-repo notes; versioned; injected at yield) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/CrossRepoGate.cs` (`IMergeGate` impl: "all repos verified" before any merge) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/EpicSlices.cs` (slice model: dependency order, per-slice controls, overlap-informed serialization) |
| **Create** | `GitLoom.App/ViewModels/Tasks/MultiRepoTaskViewModel.cs`, `TaskReviewViewModel.cs` (stitched per-repo cockpits + combined gate), `SliceBoardViewModel.cs` + views |
| **Edit** | protos (task CRUD/state stream) + daemon wiring |
| **Create** | `GitLoom.Tests/MultiRepoTaskTests.cs`, `CrossRepoGateTests.cs`, `EpicSliceTests.cs`, `SharedContextTests.cs`, `GitLoom.Tests/Integration/TwoRepoTaskEndToEndTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// GitLoom.Core/Agents/Orchestrator/MultiRepoTask.cs
public sealed record MultiRepoTask(string TaskId, string Title, IReadOnlyList<string> RepoHashes,
    string? TicketExternalId, IReadOnlyDictionary<string /*repoHash*/, string /*agentId*/> Workers);
```

One Task spans N repos: one worktree/worker per repo, a **shared context document** injected into
every worker's prompt context, per-repo verification, and a task-level review view stitching the
per-repo cockpits with a combined **"all repos verified"** gate before the (sequential, per-repo,
human) merges.

**Epic slices:** a multi-task plan may declare dependency-ordered slices (slice 2 starts after
slice 1 merges); per-slice controls: pause/resume, replan (re-approve), skip, retry; per-slice
cost + gate status from P2-08/P2-10 telemetry.

---

## 3. Implementation steps

1. **Task record + service:** create task (repos + title + optional ticket) → per-repo plan
   approval (each repo's plan through P2-14 — one approval screen may batch them, but each plan
   is a real approved plan) → spawn one worker per repo (admission-capped). Persisted in daemon
   SQLite; task state = projection of member worker/queue states (no duplicate lifecycle).
2. **Shared context document:** markdown brief (task goal, cross-repo contract notes, links);
   version counter. Injection: appended to each worker's prompt context at spawn and re-injected
   **at the next yield** after edits (never mid-generation — edge row 3); document content also
   lands in each pack (P2-34 when present).
3. **`CrossRepoGate`:** registered on every member branch's `CanMerge` — allows only when
   **every** member repo's branch is `Verified` (fresh). Merges then proceed sequentially,
   per-repo, human-gated; each merge fires that repo's stale cascade normally, and sibling
   members re-verify per ordinary P2-10 semantics.
4. **Task review view:** tabs/stitched panes of each repo's P2-11 cockpit + a combined header:
   per-repo verification status, combined gate state, spend total, radar warnings across member
   repos (each repo's `Scan` results filtered to the task's branches).
5. **Epic slices:** slice = ordered group of member tasks. Scheduler: slice N+1 spawns only when
   slice N's members are all `Merged` (or explicitly skipped). **Overlap-informed ordering:**
   before starting a slice batch, run the radar prefilter across slice-member scopes (plan file
   scopes; measured branch overlap once running) — members with predicted collisions serialize
   within the slice. Controls per slice: pause (yield+hold spawns), resume, replan (invalidate →
   re-approve), skip, retry (re-spawn failed member). Slice panel shows cost + gate chips.
6. **Failure semantics:** one repo's worker fails → task shows partial state; others unaffected
   (edge row 1). Repo directory removed from disk mid-task → typed error on that member; task
   recoverable after re-provision (edge row 2).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| one repo's worker fails | task shows partial state, others unaffected |
| repo removed from disk mid-task | typed, task recoverable |
| shared context edited mid-flight | workers get it at next yield (never mid-generation) |
| merge attempted while sibling unverified | blocked by `CrossRepoGate` with the sibling named |
| slice replan | affected members invalidated, re-approval required, later slices held |

---

## 5. Invariants (MUST)

1. **No cross-repo git operations** — each repo's boundary intact (no shared worktrees, no
   cross-repo refs).
2. The task is an **orchestration record**, not a new VCS concept — task state is derived from
   member states.
3. Plan approval per repo; the combined gate never replaces per-repo verification.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Task_SpawnsPerRepoWorkers` | 2-repo fixture → 2 approved plans, 2 workers, task record correct |
| 2 | `CrossRepoGate_BlocksUntilAllVerified` | repo A `Verified`, B `Working` → A's merge blocked naming B; both verified → allowed |
| 3 | `Task_PartialFailureStateMachine` | B's verification fails → task `Partial`, A unaffected |
| 4 | `SharedContext_InjectedAtYield` | edit doc mid-run → injection recorded only at next yield boundary |
| 5 | `Slices_DependencyOrdering` | slice 2 spawns only after slice 1 merged/skipped; pause/resume/retry transitions |
| 6 | `Slices_OverlapSerialization` | fixture with colliding scopes → members serialized within the slice |
| 7 | `TwoRepoTask_EndToEnd` (`RequiresDocker`) | brief → workers → both verified → sequential merges → task `Done` |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** any git command touching two repos; a task-level lifecycle duplicating queue
states; shared-context injection mid-generation; combined gate replacing per-repo verification.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~MultiRepoTask|FullyQualifiedName~CrossRepoGate|FullyQualifiedName~EpicSlice|FullyQualifiedName~SharedContext"
```

---

## 8. Definition of done

- [ ] Task record/service (derived state), per-repo approvals + workers, shared context with yield-boundary injection.
- [ ] `CrossRepoGate` + stitched task review view with radar/spend rollup.
- [ ] Epic slices: ordering, overlap serialization, per-slice controls + telemetry chips.
- [ ] Two-repo end-to-end green; all edge rows covered.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-28**, base `phase2`.
