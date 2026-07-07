# P2-37 — Session Checkpoints, Working-Tree Snapshots & Session Forking — Implementation Plan

**Task ID:** P2-37 · **Milestone:** M7.75 · **Priority:** P0-parity (Conductor checkpoints/fork,
Sculptor snapshots, Codex forked threads).
**Depends on:** P2-02, P2-09; **upgrades T-19** (journal gains dirty-tree snapshots).
**Branch:** implement on `feature/P2-37-session-checkpoints` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-37 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** checkpoints are **Git objects + SQLite rows, replayable offline and
> hash-chain referenced** — they survive machine moves via the repo itself and double as audit
> evidence (vs Sculptor's container-image snapshots).

---

## 0. Context — what exists today

T-19's operation journal undoes committed-state mutations but **refuses dirty trees** — the
shipped limitation this task removes. Agent sessions have no save points: a bad redirect or a
crashed CLI loses context. This task adds Git-native checkpoints (worktree sha + dirty-tree
dangling commit + transcript/env refs), restore, fork, and adapter-crash forensic resume (the
extension P2-09 deferred here).

### What you can rely on

| Fact | Where |
|---|---|
| `IOperationJournal` (T-19) — journaled mutations, undo | `GitLoom.Core/Services/OperationJournal.cs` |
| Yield discipline (checkpoints require it) + guard (mid-rebase refusal) | P2-09 |
| PTY scrollback + adapter transcript (session leader owns streams) | P2-03/P2-09 |
| Daemon SQLite; audit chain refs | P2-02/P2-15 |
| `AppDbContext` migrations (journal column addition) | `GitLoom.Core/Migrations/` |
| Worker spawn with seeded prompt context (fork replay) | P2-09/P2-14 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/SessionCheckpointService.cs` (`ISessionCheckpointService` + impl) |
| **Create** | `GitLoom.Core/Agents/TreeSnapshot.cs` (dangling-commit creation + pinned refs `refs/gitloom/snapshots/*`) |
| **Edit** | `GitLoom.Core/Services/OperationJournal.cs` + entity — snapshot SHA column (+ migration `AddJournalSnapshotSha`); snapshot before **every** journaled mutating op; undo restores uncommitted work |
| **Create** | `GitLoom.Core/Agents/CrashResume.cs` (CLI-death watcher → auto-checkpoint → resume offer) |
| **Create** | `GitLoom.App/ViewModels/Agents/CheckpointTimelineViewModel.cs` + view (list, restore, fork, labels) |
| **Edit** | protos (checkpoint CRUD/restore/fork RPCs) |
| **Create** | `GitLoom.Tests/TreeSnapshotTests.cs`, `CheckpointRoundTripTests.cs`, `ForkLineageTests.cs`, `CrashResumeTests.cs`, `JournalDirtyUndoTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// daemon GitLoom.Core/Agents/SessionCheckpointService.cs
public sealed record SessionCheckpoint(string Id, string AgentId, string WorktreeSha, string? DirtyTreeSha,
    string TranscriptRef, string EnvManifestRef, DateTimeOffset When, string Label);
public interface ISessionCheckpointService
{
    SessionCheckpoint Create(string agentId, string label);           // auto: before every user redirect; manual: button
    IReadOnlyList<SessionCheckpoint> List(string agentId);
    void Restore(string agentId, string checkpointId);                // worktree + tree + transcript context
    string Fork(string agentId, string checkpointId);                 // new worktree at checkpoint SHA + transcript replay → new agent
}
```

---

## 3. Implementation steps

1. **`TreeSnapshot` (the shared primitive):** `git stash create`-style dangling commit of the
   dirty tree (index + untracked per policy) **without touching HEAD/stash refs**; then pin it:
   `git update-ref refs/gitloom/snapshots/<id> <sha>` so `git gc` can't eat it. Pruning happens
   only via journal retention policy (delete ref + let gc collect). Pure-ish helper over the git
   runner; works on both Windows repo (T-19 upgrade) and VM worktrees (checkpoints).
2. **T-19 upgrade:** journal rows gain `SnapshotSha` (EF migration; commit migration + snapshot
   together). Before **every** journaled mutating operation: create a tree snapshot when dirty;
   undo now restores uncommitted work too (apply the snapshot commit's tree after the existing
   ref restore) — removing the clean-tree-only refusal. The previously-refused dirty case becomes
   a required test.
3. **`Create`:** requires a completed yield (P2-09 token — invariant 1); guard refuses mid-rebase
   worktrees typed (same `GitMutationGuard`). Record: `WorktreeSha` (HEAD), `DirtyTreeSha`
   (snapshot or null), transcript tail ref + PTY scrollback ref (leader buffers persisted to the
   daemon artifact store), env manifest (adapter, model, cwd, env keys — no secret values),
   label. Auto-invoked before every user redirect (steering message send); manual button in the
   timeline UI. Checkpoint ids referenced from audit events (`checkpoint_created`).
4. **`Restore`:** yield → `git reset --hard <WorktreeSha>` in the agent worktree → reapply
   `DirtyTreeSha` tree when present → re-prime adapter context (transcript summary re-injection)
   → resume. Newer commits on the branch → confirmation with a diff summary before reset (edge
   row 3). Scrollback ref GC'd → proceed without it, flagged (edge row 4).
5. **`Fork`:** new branch `agent/<newId>` + worktree at `WorktreeSha` (+ dirty tree), fresh
   adapter session seeded with the transcript summary; lineage recorded
   (`parentCheckpointId` on the new session; fork-of-fork chains — edge row 2). Returns the new
   agent id; enters the ordinary lifecycle (admission-checked).
6. **Crash resume (P2-09 extension):** the leader reports CLI death (exit without teardown —
   429-kill, OOM, crash) → `CrashResume` auto-creates a checkpoint from last-known state
   (worktree as-is + last transcript tail) → agent state `Crashed` with a "resume in a fresh
   session with reconstructed context" action = `Fork` under the hood, retiring the dead session.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| checkpoint during mid-rebase worktree | refused typed (same guard as keep-alive) |
| fork of a fork | lineage recorded (chain queryable) |
| restore with newer commits on the branch | confirmation with diff summary |
| scrollback ref GC'd | restore proceeds without it, flagged |
| undo of a mutating op on a dirty tree (T-19 upgrade) | uncommitted changes restored |

---

## 5. Invariants (MUST)

1. **No checkpoint without a completed yield.**
2. Dangling-commit snapshots are pinned via `refs/gitloom/snapshots/*` — `git gc` cannot collect
   them; pruning only via journal retention policy.
3. Env manifests never contain secret values.
4. Checkpoints are referenced from the audit chain (P2-15).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Snapshot_DanglingPinnedSurvivesGc` | snapshot → `git gc --prune=now` → object retrievable via the pinned ref |
| 2 | `Checkpoint_RestoreRoundTrip_DirtyTree` | dirty worktree → checkpoint → mutate → restore → tracked + untracked state byte-identical |
| 3 | `Journal_DirtyUndo` | mutating op on dirty tree → undo → uncommitted changes back (the old refusal case) |
| 4 | `Fork_LineageChain` | fork → fork → lineage records parent chain; new agents enter lifecycle |
| 5 | `Restore_NewerCommits_Confirms` | extra commit after checkpoint → confirmation payload includes diff summary |
| 6 | `CrashResume_FromKilledScriptedCli` (`RequiresDocker`) | kill the scripted CLI → auto-checkpoint exists → resume-fork produces a working session |
| 7 | `Checkpoint_RequiresYield_MidRebaseRefused` | guard paths typed |
| 8 | `EnvManifest_NoSecretValues` | manifest content scanned against seeded secrets |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** unpinned snapshots (gc-vulnerable); checkpoint without yield; a second snapshot
mechanism (T-19 and checkpoints must share `TreeSnapshot`); secrets in env manifests.

```bash
dotnet build GitLoom.slnx
dotnet test   # full suite — journal/GitServices surfaces touched (global rule 3)
dotnet test --filter "FullyQualifiedName~Snapshot|FullyQualifiedName~Checkpoint|FullyQualifiedName~Fork|FullyQualifiedName~CrashResume|FullyQualifiedName~JournalDirty"
grep -rn "stash push" GitLoom.Core/Agents/TreeSnapshot.cs   # 0 hits — stash create style, never the stash stack
```

---

## 8. Definition of done

- [ ] Shared `TreeSnapshot` (pinned refs) powering both checkpoints and the T-19 dirty-undo upgrade (+ migration).
- [ ] Create/List/Restore/Fork per contract with yield/guard discipline, lineage, confirmations.
- [ ] Crash forensic resume from CLI death; audit references.
- [ ] All edge rows green; full suite green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-37**, base `phase2`.
