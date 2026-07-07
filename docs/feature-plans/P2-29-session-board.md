# P2-29 — Session Board & Side-by-Side Comparison — Implementation Plan

**Task ID:** P2-29 · **Milestone:** M7.75 · **Priority:** P1-parity (Kepler kanban + comparison,
Vibe Kanban, Nimbalyst).
**Depends on:** P2-13 (agent UI shell); projects P2-10 states.
**Branch:** implement on `feature/P2-29-session-board` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-29 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Design rule:** the board is a **projection of existing state — zero new lifecycle concepts**.
> **Beat:** comparison shows **verification results and cost per candidate**, not just diffs.

---

## 0. Context — what exists today

P2-13 renders agents as a LIFO list; P2-10 owns the states; P2-08 the spend; P2-11 provenance and
the diff stack (T-13). Competitors present sessions as kanban boards and support comparing
alternative agent outputs. This task adds both views as pure projections.

### What you can rely on

| Fact | Where |
|---|---|
| `WorkerMergeState` enum + state stream + legal transitions | P2-10 `MergeQueue` |
| Conflict / RateLimited badges | P2-13 status tokens |
| T-13 diff stack (diff-vs-main panes) | `DiffViewerViewModel` |
| `VerificationRecord` (pass/fail, main sha, when) | P2-10 |
| Per-agent spend (`GetSpendSince`, snapshot) | P2-08 |
| Provenance summary per branch | P2-11 |
| Rejection path (branch delete + teardown) | P2-10 step 5 |
| Follow-up prompt into a worker (`send_worker_prompt`) | P2-14 tools |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.App/ViewModels/Agents/SessionBoardViewModel.cs` (+ `BoardColumnViewModel`, card reuse of `AgentCardViewModel`) |
| **Create** | `GitLoom.App/Views/Agents/SessionBoardView.axaml(.cs)` |
| **Create** | `GitLoom.Core/Agents/Orchestrator/BoardProjection.cs` (pure: states → columns; legal-drag map) |
| **Create** | `GitLoom.App/ViewModels/Agents/ComparisonViewModel.cs` + `Views/Agents/ComparisonView.axaml(.cs)` (2–3 branch panes) |
| **Edit** | board/comparison entry points in the activity bar / cockpit |
| **Create** | `GitLoom.Tests/BoardProjectionTests.cs`, `ComparisonViewModelTests.cs`, `WinnerPickTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **Board view:** agents/tasks as cards in state columns — the P2-10 states are the lanes:
  `Working / Verifying / Verified / AwaitingReview / Merged-Rejected` (+ Conflict/RateLimited as
  badges, not lanes). Drag between columns **only where a real transition exists** (e.g.
  `AwaitingReview → Working` with a follow-up prompt); everything else is not a drop target.
- **Comparison view:** select 2–3 agent branches → side-by-side diff-vs-main panes (T-13 stack) +
  verification records + spend + provenance summary; **"pick winner"** archives the others via
  the P2-10 rejection path.

---

## 3. Implementation steps

1. **`BoardProjection` (pure):** `Project(IEnumerable<(agentId, WorkerMergeState, badges)>) →
   columns`; `LegalDrops(state) → allowed target states with required action` — sourced from the
   P2-10 transition table (reference it, don't restate: expose the legal-transition query from
   `MergeQueue` if not already public). Merged and Rejected share a terminal column with distinct
   chips.
2. **Board VM/view:** subscribes the same agent/queue streams as P2-13 (no new RPCs); cards reuse
   `AgentCardViewModel`; column layout virtualized. Drag-drop: only `LegalDrops` targets
   highlight; `AwaitingReview → Working` drop opens the follow-up-prompt input and calls
   `send_worker_prompt` + state transition; illegal drops are inert.
3. **Comparison VM:** N (2–3) branch selections → per-candidate pane: T-13 diff-vs-main, latest
   `VerificationRecord` chip (pass/fail + freshness vs current main), spend total (P2-08 join),
   provenance/task summary. Panes scroll-locked optionally (same file list alignment when the
   branches touch similar files — nice-to-have, not binding).
4. **Winner pick:** confirm dialog naming the losers → winner untouched (proceeds to normal
   review/merge), losers → P2-10 rejection path (branch delete, sandbox prune per policy);
   audited (`merge_rejected` events with a `comparison` source tag).
5. Theming/async per v1 rules; board renders in all five themes.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| card in `Verifying` dragged anywhere | no legal targets — inert |
| `AwaitingReview → Working` drag | follow-up prompt required; cancel aborts the transition |
| comparison with a stale-verified candidate | staleness visible on the verification chip |
| winner pick with one loser mid-`Verifying` | verification cancelled, then rejected cleanly |
| agent terminates while on the board | card moves/disappears via the stream, no stale ghosts |

---

## 5. Invariants (MUST)

1. The board is a projection — **zero new lifecycle states**, no board-only state persisted
   beyond layout prefs.
2. Drag targets derive from the real transition table; the UI can never trigger an illegal
   transition.
3. Winner-pick rejections go through the standard P2-10 rejection path (audited).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Projection_FixtureStates` | fixture agents → expected columns/badges |
| 2 | `Projection_LegalDropsMatchQueueTable` | for every state: allowed targets == queue's legal transitions requiring a UI action |
| 3 | `Board_IllegalDropInert` | drop on illegal column → no RPC, no state change |
| 4 | `Comparison_ThreeFixtureBranches` | panes carry diff + verification + spend + provenance; stale chip correct |
| 5 | `WinnerPick_RejectsOthers` | losers → rejection path invoked, audit events tagged `comparison` |
| 6 | `FollowUpDrop_PromptFlow` | drag → prompt entered → `send_worker_prompt` + transition; cancel → no-op |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** board-private lifecycle state; drags wired to raw state setters; comparison
fetching diffs outside the T-13 stack; winner-pick deleting branches directly.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~BoardProjection|FullyQualifiedName~Comparison|FullyQualifiedName~WinnerPick"
grep -rn "WorkerMergeState" GitLoom.App/ | grep -v "ViewModel\|Converter"   # projection stays in VMs
```

---

## 8. Definition of done

- [ ] Pure board projection + legal-drag map derived from the queue's transition table.
- [ ] Board view (columns, badges, follow-up-prompt drop) on live streams; five themes.
- [ ] Comparison view (diff + verification + spend + provenance) and winner-pick through the rejection path.
- [ ] All edge rows tested. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-29**, base `phase2`.
