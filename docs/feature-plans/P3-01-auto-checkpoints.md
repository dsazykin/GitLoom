# P3-01 — Autonomous Git Abstraction: Auto-Checkpoints + Agent Conflict Resolution — Implementation Plan

**Task ID:** P3-01 · **Milestone:** M9 (Vibe wave) · **Priority:** P0 within the wave.
**Depends on:** P2-26 (orchestrator engine), P2-09 (yield/keep-alive), P2-37 (checkpoint
infrastructure patterns).
**Branch:** implement on `feature/P3-01-auto-checkpoints` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated scripted-swarm (checkpoints, restore, rate caps, clean conflicted state) + **real-model conflict-resolution testing required before ship**.
> All Git-side invariants are deterministic with a scripted agent. Whether a real agent resolves real conflicts acceptably is a model eval: run the induced-conflict suite against the shipping adapter(s), human-review the resolutions, and record the pass rate before enabling auto-resolution by default.
>
> **Source of truth:** §P3-01 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (K-2).
> Vibe users never see Git — checkpoints and conflict handling must be fully autonomous for
> **Vibe-managed** workers and fully absent for developer-mode ones.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P3-01 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P3-01** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P3-01 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-37 checkpoints are human-triggered session save points; P2-09 routes rebase conflicts to the
human T-04 resolver. The Vibe product needs both automated: a commit after every successful
generation loop, and agent-driven conflict resolution with escalation. The agent record needs a
**mode flag** (`Vibe | Developer`) that gates all of this.

### What you can rely on

| Fact | Where |
|---|---|
| Generation-loop completion signals (stream interception; error/ready events) | P2-26 `VibeOrchestrator` |
| Yield + keep-alive rebase + `MergeConflictException` path | P2-09 |
| Three-way blob plumbing (`GetConflictVersions`-style) + `ResolveConflict` | T-03 conflict plumbing |
| Journal (T-19) + worktree-local identity config precedent | T-19 / P2-43 (`AgentSigningConfigurator`) |
| Audit events | P2-15 |
| Escalation surface (consumes `conflict_escalated`) | P3-02 (parallel) |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/CheckpointService.cs` (`ICheckpointService`, `Checkpoint`) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/AutoConflictResolver.cs` (Vibe-only: 3-way blobs → resolve prompt → verify → finalize or escalate) |
| **Edit** | agent record/proto — `AgentMode { Developer, Vibe }` flag |
| **Edit** | `KeepAliveRebaser` (P2-09) — conflict branch: Developer → existing T-04 route; Vibe → `AutoConflictResolver` |
| **Edit** | `VibeOrchestrator` — checkpoint trigger after each successful generation loop; `VerifiedGreen` update after verification runs |
| **Create** | `GitLoom.Tests/CheckpointServiceTests.cs`, `AutoConflictResolverTests.cs`, `CheckpointRateCapTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// daemon GitLoom.Core/Agents/Orchestrator/CheckpointService.cs
public sealed record Checkpoint(string Sha, string Summary, DateTimeOffset When, bool VerifiedGreen);
public interface ICheckpointService
{
    /// <summary>Stage-all + commit "Auto-Checkpoint: <summary>" in the agent worktree after each
    /// successful generation loop. Uses a dedicated Vibe author identity; never touches user config.</summary>
    Checkpoint CreateCheckpoint(string repoHash, string agentId, string summary);
    IReadOnlyList<Checkpoint> GetCheckpoints(string repoHash, string agentId, int take = 50);
    /// <summary>Hard-restore the worktree to a checkpoint — worktree-scoped, journaled (T-19),
    /// refused with a typed error if the agent is unpaused.</summary>
    void RestoreCheckpoint(string repoHash, string agentId, string sha);
}
```

Autonomous conflict resolution: on `MergeConflictException` during keep-alive rebase of a
**Vibe-managed** worker only — 3-way blobs → structured resolve prompt to the agent CLI →
success ⇒ `ResolveConflict` + continue; failure or a second identical conflict ⇒ escalate to
P3-02. Attempts are audit events (`conflict_auto_resolved` / `conflict_escalated`).

---

## 3. Implementation steps

1. **Mode flag:** `AgentMode` on the agent record (set at spawn: Vibe sessions from the P2-26
   chat bridge / P3-03 UI; everything else Developer). All new behavior branches on it.
2. **`CreateCheckpoint`:** requires yield (the generation-loop-complete signal arrives when the
   CLI is idle — still acquire the token); `git add -A && git commit -m "Auto-Checkpoint:
   <summary>"` with **worktree-local Vibe identity** (`user.name "GitLoom Vibe"`,
   `user.email vibe@gitloom-daemon` — local config, same mechanism as P2-43; never
   user-global). Summary from the chat turn (bounded). Generation loop failed mid-write ⇒ no
   checkpoint call (orchestrator gates it; edge row 1). `VerifiedGreen`: false at creation;
   flipped by the next verification pass over that sha (P2-10 record join).
3. **Rate cap:** config `MinInterval` (default 60 s) + `MaxCheckpoints` (default 200/agent):
   below-interval requests fold into the next one; above-max prunes oldest (they are ordinary
   commits on the agent branch — "prune" = drop from the checkpoint list, history curation is
   P2-20's job; edge row 5).
4. **`RestoreCheckpoint`:** typed refusal when the agent is unpaused (edge row 2); else yield →
   `git reset --hard <sha>` (worktree-scoped) — **journaled and itself undoable** (T-19 entry;
   invariant 2). Never a `Directory` copy (rejection trigger).
5. **`AutoConflictResolver`:** on Vibe conflict: for each conflicted path, pull base/ours/theirs
   blobs (T-03) → structured prompt into the agent CLI stdin (bounded content, file-by-file) →
   agent replies with resolved content (protocol: fenced file blocks) → write + `ResolveConflict`
   (stage) → all resolved ⇒ `rebase --continue`; then verification runs — tests failing means
   the checkpoint records `VerifiedGreen=false` (edge row 3, caught downstream, not silently
   re-attempted).
   - Failure to produce parseable resolution, or the **same conflict fingerprint** (path set +
     conflict-hunk hash) a second time ⇒ `conflict_escalated` + leave the repo in a **clean
     conflicted state** (mid-rebase, markers intact, nothing half-finalized — edge row 4) for
     P3-02.
   - Guard: resolved content containing conflict markers ⇒ treated as failure (rejection
     trigger: markers-as-resolved).
6. **Audit:** `conflict_auto_resolved` (paths, fingerprint) / `conflict_escalated`; checkpoint
   create/restore events.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| generation loop fails mid-write | no checkpoint; previous checkpoint untouched |
| restore with unpaused agent | typed refusal, nothing changes |
| agent resolves conflict incorrectly (tests fail) | verification catches it; checkpoint marked `VerifiedGreen=false` |
| unresolvable conflict | escalation event; repo left in a clean conflicted state |
| checkpoint spam (agent loops fast) | rate-cap: min interval + max checkpoints, oldest pruned |

---

## 5. Invariants (MUST)

1. Checkpoints use the Vibe author identity via worktree-local config — never the user's global
   identity, never a placeholder outside the agent branch.
2. Every restore is journaled and itself undoable.
3. Auto-resolution never runs for developer-mode agents (their conflicts surface to the human
   resolver — P2-09 behavior unchanged).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Checkpoints_NTurnsNTreeValidCommits` | scripted N chat turns → N commits, each tree-valid, Vibe identity, correct summaries |
| 2 | `Checkpoint_SkippedOnFailedLoop` | failure signal → no new commit |
| 3 | `RateCap_IntervalAndMax` | fast loop → folded checkpoints; over max → oldest dropped from the list |
| 4 | `Restore_RoundTripJournaled` | restore → tree at sha; journal entry; undo restores pre-restore state |
| 5 | `Restore_UnpausedRefused` | typed, worktree untouched |
| 6 | `AutoResolve_ScriptedAgentFinalizesMerge` | induced conflict + scripted resolver → rebase completed, `conflict_auto_resolved` event |
| 7 | `AutoResolve_MarkersDetected_Fails` | scripted agent returns markers → treated as failure |
| 8 | `AutoResolve_SecondIdenticalEscalates` | same fingerprint twice → `conflict_escalated`, clean conflicted state (`rebase-merge` present, no staged half-merge) |
| 9 | `DeveloperMode_NeverAutoResolves` | Developer agent conflict → T-04 route, resolver never invoked (spy) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** auto-resolution writing conflict markers to disk as "resolved"; restore via
`Directory` copy; checkpoints with user-global identity; Vibe behavior leaking into Developer
mode.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Checkpoint|FullyQualifiedName~AutoConflictResolver"
grep -rn "Directory.Copy\|Directory.Move" GitLoom.Core/Agents/Orchestrator/CheckpointService.cs   # 0 hits
grep -rn "config --global" GitLoom.Core/Agents/Orchestrator/   # 0 hits
```

---

## 8. Definition of done

- [ ] Mode flag; auto-checkpoints (Vibe identity, rate-capped, `VerifiedGreen` join) after green loops.
- [ ] Journaled, undoable, pause-gated restore.
- [ ] Vibe-only auto-resolution with fingerprint escalation and clean conflicted state; audit events.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P3-01**, base `phase2`.
