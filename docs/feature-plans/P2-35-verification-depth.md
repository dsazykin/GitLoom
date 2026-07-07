# P2-35 — Verification Depth: Bounded Repair Loop, Diff Guard, AI Review Pass — Implementation Plan

**Task ID:** P2-35 · **Milestone:** M7.75 · **Priority:** P0 (matches MergeLoom gates 4–6).
**Depends on:** P2-10, P2-11; **amends both**.
**Branch:** implement on `feature/P2-35-verification-depth` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-35 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** the repair loop runs in a **visible terminal the human can take over
> mid-repair**, inside the default-deny sandbox, every attempt audited; Diff Guard blocks and
> **routes to the cockpit** (split via P2-20 curation) instead of discarding work.

---

## 0. Context — what exists today

P2-10 verification is one-shot: fail → back to `Working`. MergeLoom ships a repair loop, a diff
guard, and an AI reviewer as gates 4–6. This task deepens our pipeline with all three — repair
bounded and visible, Diff Guard pure and plan-aware, AI review advisory-only.

### What you can rely on

| Fact | Where |
|---|---|
| `IMergeQueue.RunVerificationAsync` + immutable records | P2-10 |
| Worker sandbox + PTY (visible terminal; human can type into an unlocked session) | P2-03/P2-07/P2-14 |
| `TaskPlan.Scope` (files[]) for off-scope detection | P2-14 |
| `FilePatch` models + flagged gate + cockpit lanes | T-06/P2-11 |
| Gateway budgets (AI review is a budgeted call) | P2-08 |
| Context packs (AI review input) | P2-34 |
| Lessons store (findings persist) | P2-36 (parallel — emit the events; storage may land there) |
| Audit events | P2-15 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Edit** | `GitLoom.Core/Agents/Orchestrator/MergeQueue.cs` — overload `RunVerificationAsync(agentId, RepairPolicy, ct)`; repair orchestration |
| **Create** | `GitLoom.Core/Agents/Orchestrator/RepairLoop.cs` (scoped repair prompt into the same worker sandbox; attempt counting; flake detection) |
| **Create** | `GitLoom.Core/Review/DiffGuard.cs` (pure) + `DiffGuardPolicy` (per-repo config) |
| **Create** | `GitLoom.Core/Review/AiReviewService.cs` (`IAiReviewService`, `ReviewFinding`) |
| **Edit** | `CanMerge` composition: DiffGuard verdict beside the flagged gate |
| **Edit** | cockpit (P2-11): AI-reviewer lane; DiffGuard block panel with "route to curation" action |
| **Create** | `GitLoom.Tests/RepairLoopTests.cs`, `DiffGuardTests.cs`, `AiReviewLaneTests.cs`, `FlakeDetectionTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// amendments
// P2-10 MergeQueue gains:
Task<VerificationRecord> RunVerificationAsync(string agentId, RepairPolicy repair, CancellationToken ct);
public sealed record RepairPolicy(int MaxAttempts /* default 2 */, bool Enabled);
// GitLoom.Core/Review/DiffGuard.cs (pure)
public sealed record DiffGuardVerdict(bool Blocked, IReadOnlyList<string> Reasons); // oversized | off-scope | generated-file bulk
public static class DiffGuard
{
    public static DiffGuardVerdict Evaluate(IReadOnlyList<FilePatch> diff, TaskPlan plan, DiffGuardPolicy policy);
}
// GitLoom.Core/Review/AiReviewService.cs
public interface IAiReviewService   // optional pass, per-repo toggle
{
    Task<IReadOnlyList<ReviewFinding>> ReviewAsync(string agentId, ContextPack pack, CancellationToken ct);
}
```

---

## 3. Implementation steps

1. **Repair loop:** on verification failure (and `repair.Enabled`):
   - **Flake check first:** re-run the bare test command once on the unchanged tree; pass ⇒ mark
     the record flaky, **no repair spawned** (edge row 2).
   - Compose the scoped repair prompt: failure log tail + failing test names + plan scope;
     inject into the **same worker sandbox's CLI stdin** (the visible PTY — the human can watch
     and take over). Await the worker settling (yield boundary), re-verify.
   - Attempts capped by `RepairPolicy` (default 2); the counter advances on every attempt even if
     a repair introduces a *new* failure — no infinite alternation (edge row 1). Each attempt →
     journal + `repair_attempted` audit event. Cap reached → normal failure surfacing.
   - **External-PR entries (P2-12): repair never runs** unless the org explicitly enables it
     (their branch, our writes).
2. **Diff Guard (pure):** rules over the merge diff vs the approved plan:
   - `oversized`: added+removed lines > policy threshold.
   - `off-scope`: files outside `plan.Scope` globs (skipped when plan-less/manual mode — only
     volume rules apply, edge row 3).
   - `generated-file bulk`: bulk changes to generated patterns (`*.designer.cs`, `dist/`,
     minified, migrations-snapshot) — lockfiles exempted into their own category (P2-11 owns
     lockfile semantics).
   Verdict feeds `CanMerge` beside the flagged gate; the cockpit block panel offers **route to
   curation** (P2-20 split) rather than discard. Thresholds per-repo `DiffGuardPolicy` with sane
   defaults.
3. **AI review pass (optional, off by default):** per-repo toggle; on `Verified`, an LLM call via
   the P2-08 gateway (budgeted; agent-class lease) over the diff + context pack → `ReviewFinding`
   list (severity, path/hunk anchor, text). Rendered as a distinct **"AI reviewer" lane** in the
   cockpit — **advisory only, never a merge gate by itself**; timeout/budget-exhaustion leaves
   verification outcome unaffected, lane shows "unavailable" (edge row 4). Findings emit events
   for the P2-36 lessons store.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| repair introduces a new failure | attempt counter still advances; no infinite alternation |
| flaky test (same hash passes on bare re-run) | flake detection marks the record, repair not spawned |
| Diff Guard on a plan-less run (manual mode) | volume rules apply; scope rules skipped |
| AI review timeout/budget-exhausted | verification outcome unaffected; lane shows "unavailable" |
| external-PR entry fails verification | no repair (unless org-enabled); ordinary failure path |

---

## 5. Invariants (MUST)

1. Repair attempts capped and audited.
2. `DiffGuard` pure + fixture-tested.
3. AI review advisory-only and budget-gated.
4. Repair edits happen only inside the worker's own worktree (rejection trigger otherwise).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Repair_SucceedsWithinCap` | scripted failing test fixed by injected prompt (scripted worker) → re-verify pass; 1 `repair_attempted` event |
| 2 | `Repair_CapReached` | persistent failure → exactly MaxAttempts attempts → failure surfaced |
| 3 | `Repair_NewFailureCounts` | alternation fixture → counter monotonic, terminates |
| 4 | `Flake_NoRepairSpawned` | bare re-run passes → record marked flaky, zero repair prompts |
| 5 | `DiffGuard_Corpus` | oversize, off-scope, generated-bulk, lockfile-exempt, plan-less fixtures → exact verdicts/reasons |
| 6 | `DiffGuard_FeedsCanMerge` | blocked verdict → merge blocked with reasons; route-to-curation action available |
| 7 | `AiReview_LaneFromFixture` | fixture findings render in the lane; toggle off → no call (gateway spy) |
| 8 | `AiReview_UnavailableGraceful` | timeout → lane "unavailable", `Verified` state intact |
| 9 | `ExternalPr_NoRepairDefault` | P2-12 entry failure → no injection |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** repair editing outside the worker's worktree; AI review as a hard gate; unbounded
attempts; DiffGuard logic in the UI.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~RepairLoop|FullyQualifiedName~DiffGuard|FullyQualifiedName~AiReview|FullyQualifiedName~Flake"
grep -rn "DiffGuard" GitLoom.App/ | grep -v ViewModel   # rendering only
```

---

## 8. Definition of done

- [ ] Bounded, audited, visible repair loop with flake detection; external-PR exclusion.
- [ ] Pure plan-aware DiffGuard wired into `CanMerge` with route-to-curation.
- [ ] Optional advisory AI-review lane (budgeted, graceful degradation, lessons events).
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-35**, base `phase2`.
