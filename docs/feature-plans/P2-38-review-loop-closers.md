# P2-38 — Review Loop-Closers: Inline Comments → Agent, Viewed-State Receipts, Curation-Surviving Review State — Implementation Plan

**Task ID:** P2-38 · **Milestone:** M7.75 · **Priority:** P0-parity (Codex diff comments,
Conductor mark-viewed; the receipts are novel).
**Depends on:** P2-11, P2-09; T-13 diff stack.
**Branch:** implement on `feature/P2-38-review-loop-closers` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-38 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** the repo-level coverage report ("lines merged with zero human eyes", by
> agent/model/date) is the EU-AI-Act artifact nobody else can produce; curation never resets
> review progress.

---

## 0. Context — what exists today

The cockpit (P2-11) ranks and gates but reviewing is read-only: no way to tell the agent "fix
this line", no record of what a human actually looked at, and any history rewrite (P2-20) would
orphan whatever state existed. This task closes all three loops, keyed by `git patch-id` so state
survives rebases.

### What you can rely on

| Fact | Where |
|---|---|
| Diff views (unified + split) + ranked hunk list | T-13 / P2-11 cockpit |
| Steering channel into a worker (`send_worker_prompt`) + message queue when busy | P2-09/P2-14 (queue arrives with P2-39 — degrade to immediate-or-typed-busy until then) |
| Review-sprint mode emitting viewed-state events | P2-11 §3.7 |
| Audit chain (receipts) | P2-15 |
| Curation planner/executor (patch-id mapping hook) | P2-20 |
| Provenance (agent/model per hunk) for the coverage report joins | P2-11 |
| Blame (line → merged-commit joins for repo-level coverage) | T-11 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Review/ReviewSession.cs` (`IReviewSessionService`, `DiffComment`, `HunkViewedReceipt`, `ReviewCoverage`) |
| **Create** | `GitLoom.Core/Review/PatchIdCalculator.cs` (stable `git patch-id` per hunk/file-patch via the runner) |
| **Create** | `GitLoom.Core/Review/ReviewStateMigrator.cs` (curation/rebase: old→new patch-id/content-hash mapping) |
| **Create** | `GitLoom.Core/Review/CoverageReport.cs` (pure: receipts + merge history → repo-level coverage) |
| **Edit** | diff view(s): comment gutter (add/edit/delete, send-to-agent), viewed checkbox per hunk; ranked list shows viewed progress |
| **Edit** | P2-20 executor: invoke `ReviewStateMigrator` after rewrite |
| **Create** | `GitLoom.App/ViewModels/Review/ReviewCoverageViewModel.cs` + view (repo report) |
| **Create** | `GitLoom.Tests/PatchIdStabilityTests.cs`, `CommentSteeringTests.cs`, `CurationMigrationTests.cs`, `CoverageReportTests.cs`, `ReceiptAuditTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// GitLoom.Core/Review/ReviewSession.cs
public sealed record DiffComment(string Id, string Path, string PatchId /* git patch-id: rebase-stable */,
    int Line, string Text, string Author, DateTimeOffset When, bool SentToAgent);
public sealed record HunkViewedReceipt(string PatchId, string ReviewerIdentity, string AtCommit, DateTimeOffset When);
public interface IReviewSessionService
{
    DiffComment AddComment(string agentId, DiffComment comment);
    void SendToAgent(string agentId, string commentId);        // serialized as a steering message (file:line + hunk context)
    void MarkViewed(string agentId, string patchId, bool viewed);   // emits a HunkViewedReceipt (audited)
    ReviewCoverage GetCoverage(string agentId);                 // % hunks viewed, by risk category
}
```

---

## 3. Implementation steps

1. **Patch-id keying:** `PatchIdCalculator` pipes a hunk's patch text through
   `git patch-id --stable` (runner) with caching. Every comment/receipt keys on it — whitespace/
   offset-shifts from rebases keep the id stable; content changes give a new id (correct: a
   changed hunk needs re-review).
2. **Comment gutter:** gutter affordance in unified + split views; session-scoped store (daemon
   SQLite per agent branch). "Send to agent": serialize as a steering message —
   `Review comment on <path>:<line>:\n<hunk context>\n<text>` — via the P2-09 steering channel
   (queued if busy once P2-39 lands; until then: deliver-on-idle or typed busy). The agent's
   follow-up commit links back (`Fixes-Comment: <id>` trailer via the adapter commit path /
   trace ref) so the cockpit renders comment → fix. **Send-to-agent never writes to the worktree
   itself** (invariant 2).
3. **Mark-viewed + receipts:** checkbox per hunk (and sprint-mode keystroke, P2-11); every mark
   emits a `HunkViewedReceipt` **append-only on the audit chain** (unmark = a new event, never a
   deletion). Ranked list shows viewed progress per risk category (`GetCoverage`).
4. **Repo-level coverage report:** pure `CoverageReport.Build(receipts, mergedCommits,
   provenance)` → "lines merged with zero human eyes" by agent/model/date; rendered per repo;
   exportable (CSV/JSON). Joins: merged hunks (patch-ids at merge time) × receipts × provenance.
5. **Curation-surviving state:** P2-20 rewrite completes → `ReviewStateMigrator` maps old
   patch-ids to new: exact patch-id match first, then content-hash of the hunk body (whitespace-
   normalized) for squash-merged hunks; comments/viewed-state/Agent-Trace references re-key to
   the surviving ids; unmatched → orphaned-but-preserved (visible in an "orphaned" drawer with
   their original context, edge row 1).
6. **Two-reviewer readiness:** receipts carry `ReviewerIdentity` (P2-23 identity when present;
   local user otherwise); coverage math groups per identity (P3-10 consumes this later).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| comment on a hunk that disappears after rebase | orphaned view with context preserved |
| two reviewers | receipts per identity |
| patch-id collision (identical hunks) | both marked, acceptable (documented) |
| unmark viewed | new receipt event; history append-only |
| agent busy on send-to-agent | queued/deferred, never lost; typed state visible |

---

## 5. Invariants (MUST)

1. Receipts are append-only audit events.
2. "Send to agent" never writes to the worktree itself.
3. Coverage math is pure + fixture-tested.
4. Curation/rebase never resets review progress (migration mandatory in the P2-20 path).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `PatchId_StableAcrossRebase` | fixture branch rebased (offsets shift) → same patch-ids; content edit → new id |
| 2 | `Comment_SteeringSerialization` | comment → steering message contains path:line + hunk context; `SentToAgent` set; no worktree writes (spy) |
| 3 | `CommentFix_Linkback` | follow-up commit with trailer → cockpit pairs comment→fix |
| 4 | `Receipts_AppendOnlyAudited` | mark/unmark → two chained events; audit verify passes |
| 5 | `Curation_MigratesState` | squash fixture → comments + viewed-state survive on new ids; unmatched → orphaned drawer |
| 6 | `Coverage_ReportFixtures` | receipts+merges+provenance fixtures → exact "zero human eyes" lines by agent/date |
| 7 | `Coverage_PerIdentity` | two identities → grouped output |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** review state keyed by line numbers or commit shas (breaks on rebase); receipt
deletion/update paths; coverage logic in the UI; curation path skipping migration.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~PatchId|FullyQualifiedName~CommentSteering|FullyQualifiedName~CurationMigration|FullyQualifiedName~Coverage|FullyQualifiedName~Receipt"
grep -rn "patch-id" GitLoom.Core/Review/PatchIdCalculator.cs   # --stable present
```

---

## 8. Definition of done

- [ ] Patch-id-keyed comments (gutter, send-to-agent via steering, fix link-back).
- [ ] Append-only audited receipts + per-category progress + repo-level coverage report (exportable).
- [ ] Curation migration wired into P2-20; orphan preservation.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-38**, base `phase2`.
