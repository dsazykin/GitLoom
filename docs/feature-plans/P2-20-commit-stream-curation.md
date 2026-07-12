# P2-20 — Agent Commit-Stream Curation — Implementation Plan

**Task ID:** P2-20 · **Milestone:** M7.5 · **Priority:** P1
**Depends on:** P2-09; reuses T-08 `InteractiveRebaseService` + T-31 conventional-commit builder
(both on main).
**Branch:** implement on `feature/P2-20-commit-stream-curation` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — pure planner fixtures + real-rebase integration (`RequiresGitCli`); no human step.
>
> **Source of truth:** §P2-20 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Why:** agents produce checkpoint noise ("wip: sync", 40 micro-commits). One-click "squash
> agent checkpoints into N reviewable conventional commits" is pure Git surgery — exactly what a
> wrapper tool cannot build without re-implementing a Git client.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-20 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-20** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-20 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-09's keep-alive cycle litters agent branches with `wip: sync` commits; agent CLIs add their own
micro-commits. T-08 ships a full interactive-rebase engine (`IInteractiveRebaseService`,
`RebaseTodoItem`); T-31 ships `ConventionalCommitBuilder`. This task adds a **pure planner** that
computes a curation todo list, a preview UI, and execution through the existing engine under
P2-09's yield discipline.

### What you can rely on

| Fact | Where |
|---|---|
| `IInteractiveRebaseService` + `RebaseTodoItem` (pick/squash/fixup/reword) — the **only** rebase driver (G-7) | `GitLoom.Core/Services/InteractiveRebaseService.cs` |
| Conventional-commit builder (types, scopes, subject rules) | T-31 commit-composer surface (`CommitComposerViewModel` + Core builder) |
| Yield token requirement for worktree mutations | P2-09 `YieldProtocol` |
| Queue state (`AwaitingReview`), staleness on history rewrite | P2-10 |
| Provenance trailers `Agent:/Task:/Plan:` on agent commits | P2-11 emitter |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/CommitCurator.cs` (pure planner + `CurationOptions`) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/CurationExecutor.cs` (yield → T-08 engine → staleness notify) |
| **Create** | `GitLoom.App/ViewModels/Agents/CurationPreviewViewModel.cs` + `Views/Agents/CurationPreviewView.axaml(.cs)` (before/after commit list) |
| **Edit** | cockpit/queue UI: "Curate history" action on `AwaitingReview` branches |
| **Create** | `GitLoom.Tests/CommitCuratorTests.cs`, `CurationExecutorTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// GitLoom.Core/Agents/Orchestrator/CommitCurator.cs
public sealed record CurationPlan(IReadOnlyList<RebaseTodoItem> Todo, string Summary);
public static class CommitCurator     // pure planner; execution goes through IInteractiveRebaseService
{
    /// <summary>Folds wip/checkpoint commits into their nearest meaningful ancestor and rewords
    /// surviving messages to conventional-commit form (via ConventionalCommitBuilder).</summary>
    public static CurationPlan Plan(IReadOnlyList<(string Sha, string Message)> branchCommits, CurationOptions options);
}
```

UI (binding): on an `AwaitingReview` agent branch — "Curate history" preview (before/after commit
list) → executes via the existing T-08 engine against the worktree (yielded, P2-09 discipline) →
verification re-runs (history rewrite ⇒ stale by definition).

---

## 3. Implementation steps

1. **Planner heuristics (`CommitCurator.Plan`):**
   - Checkpoint detection: message matches `^wip\b`/`^checkpoint\b` (configurable regex list in
     `CurationOptions`), or empty-ish messages agents emit. Checkpoints become `fixup` into the
     **nearest meaningful ancestor** (walk toward the branch root; a leading run of checkpoints
     with no meaningful ancestor folds forward into the first meaningful descendant — or, if the
     branch is only checkpoints, everything squashes into one commit with a generated conventional
     subject: edge row 1).
   - Surviving commits: `reword` to conventional form via `ConventionalCommitBuilder` (type
     inferred from the commit's paths — reuse T-31's inference if present; otherwise default
     `chore:` + original subject, flagged in the preview for manual edit).
   - **Trailer preservation:** provenance trailers from folded commits are merged (deduped) onto
     the surviving commit's message — losing provenance is a planner bug (test 3).
   - Output `Summary`: human line like "12 commits → 3 (9 checkpoints folded)".
2. **Guards:** merge commit anywhere in range → typed refusal (same T-08 v1 restriction, edge
   row 2). Curation only offered for `AwaitingReview`/paused branches — a running agent blocks it
   (edge row 3).
3. **`CurationExecutor`:** acquire yield token (P2-09) → `IInteractiveRebaseService` executes the
   todo against the agent worktree → on success notify P2-10 (branch → unverified/`Working`,
   re-verify before merge — edge row 4) → release. Conflicts during rebase → same T-08 conflict
   surface, curation abortable (`--abort` restores — executor exposes abort explicitly here, this
   is a *user-initiated* flow unlike keep-alive).
4. **Preview UI:** two columns (current commits / resulting commits with reworded messages,
   editable before execute); executes only from the preview's confirm. Design tokens; async off
   UI thread.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| branch of only wip commits | single squashed commit with a generated conventional subject |
| merge commit in range | curation refused (same T-08 v1 restriction), typed |
| curation while agent running | blocked — only `AwaitingReview`/paused branches |
| post-curation | P2-10 marks the branch unverified; re-verify before merge |
| leading checkpoint run (no meaningful ancestor) | folds forward into first meaningful commit |

---

## 5. Invariants (MUST)

1. Planner is pure and fixture-tested.
2. Execution exclusively via `IInteractiveRebaseService` — no second rebase driver (G-7 heritage).
3. Provenance trailers (P2-11) preserved onto squashed results.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Plan_FoldsWipIntoAncestor` | fixture list → fixups target nearest meaningful ancestor; summary correct |
| 2 | `Plan_AllWip_SingleSquash` | only checkpoints → one commit, generated conventional subject |
| 3 | `Plan_PreservesTrailers` | folded commits' `Agent:/Task:/Plan:` trailers present (deduped) on survivor message |
| 4 | `Plan_RewordsToConventional` | surviving non-conforming subjects → conventional form; unknown type flagged |
| 5 | `Plan_MergeCommit_Refuses` | merge sha in range → typed refusal |
| 6 | `Executor_YieldedAndStalenessNotify` | execute on fixture worktree branch → history rewritten, P2-10 notified (state `Working`), yield token used (spy) |
| 7 | `Executor_RunningAgent_Blocked` | non-paused agent → typed refusal, no rebase started |
| 8 | `Executor_ConflictAbortRestores` | induced conflict → abort → branch tip unchanged |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second rebase driver; curation without a yield token; dropping trailers;
auto-executing without the preview confirm.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~CommitCurator|FullyQualifiedName~CurationExecutor"
grep -rn "rebase -i\|rebase --interactive" GitLoom.Core/Agents/   # 0 hits — engine calls only
```

---

## 8. Definition of done

- [ ] Pure planner (folding, forward-fold, all-wip squash, conventional rewording, trailer preservation) fixture-tested.
- [ ] Executor under yield discipline via T-08; staleness handoff to P2-10; abort path.
- [ ] Preview UI with editable messages on `AwaitingReview` branches.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-20**, base `phase2`.
