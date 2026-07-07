# P2-C4 — Working-Copy Power Tools: Split-Into-Branches Wizard & Stacked-Branch Restacking — Implementation Plan

**Task ID:** P2-C4 · **Track:** client parity · **Priority:** P1 (matches GitButler's
jobs-to-be-done at 10% of the cost).
**Depends on:** T-06 patch model, T-08 rebase, T-19 journal (all shipped), P2-37 tree snapshots.
**Branch:** implement on `feature/P2-C4-split-branches-restacking` off `phase2`; PR targets
`phase2`.

> **Source of truth:** §P2-C4 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Scope fence:** explicitly **not** a persistent virtual-branch working mode — the wizard is a
> one-shot operation; branches are ordinary Git branches afterwards.

---

## 0. Context — what exists today

T-06 shipped `PatchParser`/`PatchBuilder` (hunk-level staging); T-08 the rebase engine; T-19 the
journal; P2-37 pinned tree snapshots. A mixed working tree ("I did three things at once") and
stacked-branch workflows are GitButler's two headline jobs — both are compositions of shipped
plumbing.

### What you can rely on

| Fact | Where |
|---|---|
| `PatchParser` / `PatchBuilder` / `FilePatch`/`DiffHunk` (partial apply/reset) | T-06, `GitLoom.Core/Services/PatchParser.cs`, `PatchBuilder.cs` |
| `IInteractiveRebaseService` (the only rebase driver) | T-08 |
| `IOperationJournal` + `TreeSnapshot` (pinned dangling commits) | T-19 / P2-37 |
| T-04 conflict resolver against a repo path | conflict stack |
| Branch CRUD + graph rendering (labels/lanes) | `IGitService`, graph stack |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Services/SplitPlanner.cs` (pure: uncommitted diff → proposed groups by path/hunk clustering) |
| **Create** | `GitLoom.Core/Services/SplitExecutor.cs` (sequential apply/commit/reset cycles; journaled; snapshot-protected) |
| **Create** | `GitLoom.Core/Services/StackService.cs` (`IStackService`: declare B stacked-on A; restack on A movement; persistence in `AppDbContext` + migration) |
| **Create** | `GitLoom.App/ViewModels/SplitWizardViewModel.cs` (+ group editing) + `Views/SplitWizardView.axaml(.cs)` |
| **Edit** | graph rendering — stack visualization (stacked-branch indicator/edges); branch context menu ("Stack on…", "Restack now") |
| **Create** | `GitLoom.Tests/SplitPlannerTests.cs` (property tests), `SplitExecutorTests.cs`, `StackServiceTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

1. **Split wizard:** cluster uncommitted changes by path/hunk (T-06 `PatchParser`/`PatchBuilder`)
   into N proposed groups; the user adjusts groupings; each group commits to its **own new
   branch** (sequential apply/commit/reset cycles, journaled, tree-snapshot-protected via
   P2-37).
2. **Stacked branches:** mark branch B as stacked on A; when A moves (merge/amend),
   **auto-restack** B (T-08 plumbing, T-19 safety net); stack visualization on the shipped
   graph; restack conflicts route to the T-04 resolver.

**Invariants:** the wizard never loses a hunk (**sum of groups == original diff**,
property-tested); restack is always undoable; **no daemon dependency** (pure client feature).

---

## 3. Implementation steps

1. **`SplitPlanner` (pure):** input = the working diff as `FilePatch[]`. Clustering heuristics:
   directory affinity (same top-2 path segments), file-type affinity, hunk adjacency (same file
   never splits across groups by default — hunk-level moves are a user action, supported but not
   auto-proposed). Output `SplitPlan { Groups: [{Name-suggestion, FilePatches/hunks}] }` +
   completeness proof (every input hunk appears exactly once). Suggested group names from
   dominant path (`feature/split-<dir>`).
2. **Wizard UI:** three panes — group list (rename branch names, add/remove groups), file/hunk
   tree (drag between groups; ungrouped bucket must be empty to proceed), per-group diff
   preview (T-13). Commit-message field per group (T-31 conventional composer embedded).
3. **`SplitExecutor`:** pre-flight: P2-37 `TreeSnapshot` (pinned) + one T-19 journal entry
   bracketing the whole split. For each group in order: create branch from HEAD → apply the
   group's patch (`PatchBuilder` → `git apply --cached` route as in partial staging) → commit
   (group message) → reset the applied hunks from the working tree. After all groups: working
   tree empty of split hunks; original branch untouched (groups branch from it). Any step fails
   → restore from the snapshot (automatic), journal marks aborted (edge row 2).
4. **`StackService`:** persistence: `StackedBranch { Branch, BaseBranch }` rows (migration).
   Detection: on `RepositoryChanged`/post-operation, if A's tip moved and B is stacked on A →
   offer/auto (setting) restack: `rebase --onto newA oldA B` via the T-08 engine, journaled;
   conflicts → T-04 resolver, restack resumable/abortable. Amend of A (tip rewrite) is the same
   path (edge row 4). Chains (C on B on A) restack in topological order.
5. **Graph visualization:** stacked branches get a visual indicator (chip/edge linking to the
   base label). Context menu: "Stack on…", "Unstack", "Restack now".

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| every hunk assigned (property) | sum of group patches == original diff, byte-accounted |
| apply failure mid-split (conflicting group ordering) | snapshot restore; tree byte-identical to pre-split |
| binary/untracked files in the diff | grouped at file granularity; untracked included via add-then-apply path |
| A amended (not merged) | restack detects tip rewrite, `--onto` correct |
| restack conflict | T-04 resolver flow; abort restores B (journal) |
| stacked chain (C→B→A) | topological restack order; partial failure leaves consistent state + resume |

---

## 5. Invariants (MUST)

1. The wizard never loses a hunk — completeness property-tested.
2. Restack (and split) always undoable — journal + pinned snapshot.
3. No daemon dependency; everything runs in-process on the Windows repo.
4. Rebase execution exclusively via `IInteractiveRebaseService` (no second driver).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Planner_PartitionCompleteness` (property) | random synthetic diffs → groups partition the hunk set exactly (no loss, no duplication) |
| 2 | `Planner_ClusteringHeuristics` | fixture mixed diff → expected directory/type groups |
| 3 | `Executor_GroupCommitRoundTrip` | 3-group plan → 3 branches each containing exactly its hunks; working tree clean; original branch untouched |
| 4 | `Executor_FailureRestoresSnapshot` | induced apply failure → tree byte-identical; journal aborted entry |
| 5 | `Stack_RestackOnMergeAndAmend` | A merged / A amended fixtures → B replayed onto new A |
| 6 | `Stack_ConflictRoutesAndAborts` | conflicting restack → resolver state; abort restores B |
| 7 | `Stack_ChainTopologicalOrder` | C→B→A chain restacks in order |
| 8 | `Undo_SplitAndRestack` | T-19 undo restores pre-operation state incl. uncommitted changes (P2-37 dirty undo) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a persistent virtual-branch mode; hunk loss under any grouping; a second rebase
driver; daemon/proto dependencies; split without snapshot protection.

```bash
dotnet build GitLoom.slnx
dotnet test   # full suite — GitServices-adjacent (global rule 3)
dotnet test --filter "FullyQualifiedName~SplitPlanner|FullyQualifiedName~SplitExecutor|FullyQualifiedName~StackService"
grep -rn "GitLoom.Protos\|DaemonClient" GitLoom.Core/Services/SplitPlanner.cs GitLoom.Core/Services/StackService.cs   # 0 hits
```

---

## 8. Definition of done

- [ ] Pure planner with completeness property; wizard with drag-grouping + per-group composer.
- [ ] Snapshot-protected journaled executor; failure restores byte-identical tree.
- [ ] Stack declarations (persisted), auto/offered restack incl. chains, resolver routing, graph visualization.
- [ ] All edge rows green; full suite green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-C4**.
