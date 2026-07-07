# P2-10 — Merge Queue + Verification Runs + Stale Invalidation — Implementation Plan

**Task ID:** P2-10 · **Milestone:** M7 · **Priority:** P0 — **the product spine** (lead feature
per the July-2026 viability research).
**Depends on:** P2-09.
**Branch:** implement on `feature/P2-10-merge-queue` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-10 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Positioning constraint that shapes the design:** GitHub's server-side queue is CI-bound,
> PR-only, GitHub-hosted, agent-blind. This queue works **pre-PR, locally, across N agent
> branches, without CI round-trips, on any host** — and it must also serve external PR entries
> (P2-12), so the queue keys on a **branch**, not on a PTY.

---

## 0. Context — what exists today

P2-09 can yield agents and rebase their worktrees onto main. Nothing decides *when work is safe
to merge*. This task ships the state machine at the center of the product: every agent branch is
verified (project test command, in its own sandbox) against a specific `main@<sha>`; any merge to
main invalidates every other `Verified` branch and auto re-queues it. The human gate stays: no
auto-merge, ever.

### What you can rely on

| Fact | Where |
|---|---|
| Yield + keep-alive rebase (`NotifyMainMoved` re-entry hook promised there) | `GitLoom.Core/Agents/Orchestrator/KeepAliveRebaser.cs` (P2-09) |
| Sandbox exec (`SandboxEngine.ExecAsync`) for running test commands in the worker's container | P2-07 |
| Daemon SQLite (persisted state; spend ledger pattern) | P2-02/P2-08 |
| Windows-side journaled operations (T-19 `IOperationJournal`) — undoable foreground merge | `GitLoom.Core/Services/OperationJournal.cs` / `IOperationJournal.cs` |
| `gitloom-vm` remote registered on the Windows repo | P2-06 |
| Typed exceptions; `ExecuteWithRepo` on the Windows side | `GitLoom.Core/Services/GitServices.cs` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/MergeQueue.cs` (`IMergeQueue`, state machine, persistence) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/VerificationRunner.cs` (test cmd in the worker sandbox; log artifact capture) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/VerificationStore.cs` (immutable records, daemon SQLite) |
| **Create** | `GitLoom.Core/Services/ForegroundMergeService.cs` + interface (Windows side: fetch + merge + journal) |
| **Edit** | `GitLoom.Server/Services/AgentGrpcService.cs` / new `MergeQueueGrpcService` RPCs (state stream, run verification, can-merge query) + proto additions |
| **Edit** | P2-09 `KeepAliveRebaser` wiring (`NotifyMainMoved` → yield → rebase → re-verify) |
| **Create** | `GitLoom.App/ViewModels/MergeQueueViewModel.cs` + view (queue panel: states, merge button, override affordance) |
| **Create** | `GitLoom.Tests/MergeQueueStateMachineTests.cs`, `VerificationRunnerTests.cs`, `ForegroundMergeServiceTests.cs`, `GitLoom.Tests/Integration/StaleCascadeTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// daemon GitLoom.Core/Agents/Orchestrator/MergeQueue.cs
public enum WorkerMergeState { Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected }
public sealed record VerificationRecord(string AgentId, string MainSha, bool Passed, string LogArtifactPath, DateTimeOffset When);
public interface IMergeQueue
{
    WorkerMergeState GetState(string agentId);
    Task<VerificationRecord> RunVerificationAsync(string agentId, CancellationToken ct); // test cmd in the agent's sandbox
    void NotifyMainMoved(string newMainSha);        // marks every Verified worker StaleVerified + auto re-queues
    bool CanMerge(string agentId, out string reason); // false when stale/unverified (settings override, loudly labeled)
}
```

Windows side: `ForegroundMergeService` — "Merge to Main" =
`git fetch gitloom-vm && git merge agent/<id>` on the Windows repo (human-gated, journaled via
T-19); post-merge installs run `--ignore-scripts` wrapped in retry (NTFS `EPERM`/`EBUSY`).

---

## 3. Implementation steps

### 3.1 State machine + persistence (step 1)

Transitions (exhaustive; anything else is invalid and throws typed):

```
Working      → Verifying            (RunVerification)
Verifying    → Verified | Working   (pass | fail — failure surfaced, not silently retried)
Verified     → StaleVerified        (NotifyMainMoved)
Verified     → AwaitingReview       (review requested / cockpit opens it)
AwaitingReview → Merged | Rejected  (human decision)
StaleVerified → Verifying           (auto re-queue: yield → keep-alive rebase → re-verify)
Rejected     → (teardown per policy)
any          → Working              (new commits from the agent invalidate)
```

Persist `(agentId, state, lastVerification)` in daemon SQLite inside the same transaction as the
transition — a daemon restart resumes queue state; an interrupted `Verifying` restarts or resumes
the run, **never stuck** (edge row 4).

### 3.2 Verification runs (step 2)

- Test command = the project's configured verification command (per-repo setting; absent →
  typed "no verification command configured"; merge then allowed only via the explicit unverified
  override — edge row 5).
- Run **in the worker's own sandbox** (`SandboxEngine.ExecAsync` in the agent's container,
  cwd = its worktree). Host execution is a rejection trigger.
- Record: `main@<sha>` (mirror main at run start) + pass/fail + full log captured to an artifact
  file under the daemon's artifact dir; `VerificationRecord` rows are **immutable** — re-runs
  insert new rows (invariant 2).

### 3.3 The stale cascade (step 3 — densest tests in the milestone)

`NotifyMainMoved(newMainSha)`:

1. Every `Verified` (and `AwaitingReview`-but-verified) worker flips `StaleVerified`.
2. Each auto re-enters: P2-09 yield → keep-alive rebase onto new main → `RunVerificationAsync`
   → `Verified` (or `Working` on failure/conflict, surfaced).
3. Re-queue ordering FIFO by original verification time; concurrency capped (one verification per
   agent at a time; global cap = admission-controlled).

Callers: `ForegroundMergeService` fires it after every successful human merge (via a daemon RPC);
P2-12 merges fire it too.

### 3.4 Merge gating + override (step 4)

`CanMerge` false when state ≠ `Verified`/fresh (or flagged changes unacknowledged — P2-11 wires
its detector in here later; leave a composable predicate hook
`IMergeGate { bool Allows(agentId, out reason) }`, the queue owning the staleness gate).
The override setting exists but: loud warning label in the UI, journaled on the Windows side,
audit event (`stale_override_used`) emitted (G-17; plain journal row until P2-15 chains it).

### 3.5 `ForegroundMergeService` (Windows side)

- `MergeAgentBranch(repoPath, agentId)`: `git fetch gitloom-vm` then `git merge agent/<id>`
  (LibGit2Sharp merge or CLI per the G-7 policy split — merge is a read-modify op the existing
  merge path already implements; **reuse the existing merge service surface**, journaled via
  T-19 so it is undoable).
- Post-merge dependency install (when lockfile present): `--ignore-scripts` **always**, wrapped
  in NTFS retry (`EPERM`/`EBUSY` backoff). The poisoned-`postinstall` canary test asserts scripts
  do not execute.
- On success → daemon `NotifyMainMoved(newSha)`.

### 3.6 Queue panel (UI)

`MergeQueueViewModel`: rows (agent, state badge, last verification age + main-sha match, merge
button bound to `CanMerge` with reason tooltip, override behind a confirm + warning). States
stream over gRPC. Design tokens/component classes; all five themes.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| A merges while B and C are `Verified` | B, C → `StaleVerified`, auto re-queue, re-verify against new main |
| verification fails after rebase | worker back to `Working` with the failure surfaced, not silently retried |
| merge attempted on stale verification | blocked; override path logged + audited + labeled |
| daemon restart mid-`Verifying` | run restarts or resumes; state never stuck |
| test command absent | typed "no verification command configured"; merge allowed only with the explicit unverified override |

---

## 5. Invariants (MUST)

1. A merge through the UI on a fresh `Verified` state is the **only silent path**; every other
   path warns and records.
2. Verification results are immutable records tied to a `main@<sha>`; re-verification creates a
   new record.
3. The human foreground merge happens on the Windows repo via the existing journaled service
   surface (undoable via T-19).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `StateMachine_ExhaustiveTransitions` | every legal transition; every illegal transition throws typed; property test over random legal sequences never corrupts state |
| 2 | `StaleCascade_TwoVerifiedWorkers` | A merges → B,C `StaleVerified` → re-queued FIFO → re-verified against new sha |
| 3 | `Override_LoggedAuditedLabeled` | stale merge via override → journal row + audit event + `CanMerge` still false (override is a separate path) |
| 4 | `Restart_ResumesQueue` | kill queue mid-`Verifying` (in-proc) → reload from SQLite → run restarted, terminal state reached |
| 5 | `NoTestCommand_TypedAndOverrideOnly` | edge row 5 exactly |
| 6 | `VerificationRecord_Immutable` | re-run inserts; prior row untouched (store API has no update) |
| 7 | `TwoScriptedWorkers_EndToEnd` (`RequiresDocker`) | integration: A merges → B re-verifies → merge button blocked until fresh |
| 8 | `IgnoreScriptsCanary` | fixture package with poisoned `postinstall` → merge + install → script did **not** execute (marker file absent) |
| 9 | `ForegroundMerge_JournaledUndoable` | merge → T-19 journal entry exists → undo restores pre-merge HEAD |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** **auto-merge of any kind** — the human gate is the product thesis; verification
run outside the worker's sandbox (host execution); mutable verification records; a merge path
skipping the journal.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~MergeQueue|FullyQualifiedName~Verification|FullyQualifiedName~ForegroundMerge|FullyQualifiedName~StaleCascade"
dotnet test   # full suite — this task touches GitServices-adjacent surfaces (global PR rule 3)
grep -rn "install" GitLoom.Core/Services/ForegroundMergeService.cs | grep -v "ignore-scripts"  # every install guarded
```

---

## 8. Definition of done

- [ ] State machine exact enum, persisted transitions, restart-resume.
- [ ] Sandbox-executed verification with immutable `main@<sha>` records + log artifacts.
- [ ] Stale cascade: `NotifyMainMoved` → re-queue → re-verify, integration-proven with two workers.
- [ ] Human-gated `ForegroundMergeService` (journaled, `--ignore-scripts` + NTFS retry, canary green); override loud + recorded.
- [ ] Queue panel streaming states; composable merge-gate hook for P2-11.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-10**, base `phase2`.
