# P2-09 — Agent Lifecycle: Cooperative Yield + Keep-Alive Rebase — Implementation Plan

**Task ID:** P2-09 · **Milestone:** M7 · **Priority:** P0
**Depends on:** P2-06 (worktrees), P2-07 (sandboxes).
**Branch:** implement on `feature/P2-09-agent-lifecycle` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — scripted-swarm on `DualRepoFixture` + Docker for pause/leader cases; no human step.
> `ScriptedAgentHarness` makes yield/keep-alive/teardown/leader-reattach deterministic; a real coding CLI is not needed to prove any invariant here.
>
> **Source of truth:** §P2-09 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.3 steps 1–2, 7–8). The 2026-07-07 extension note (adapter-crash forensic resume)
> is **specified in P2-37 step 4** — this task only provides the yield/leader plumbing it rides
> on; do not implement resume UI here.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-09 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-09** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-09 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-06/P2-07 give each agent a worktree and a hardened container; P2-03 gives it a PTY. Nothing
yet coordinates the daemon touching a worktree **while the agent is alive** — the exact
`index.lock`-collision class of bug this product exists to prevent, now on the agent side. This
task ships the four lifecycle mechanisms: cooperative yield, keep-alive rebase, session
durability, and teardown.

### What you can rely on

| Fact | Where |
|---|---|
| `PtySession` (IO stream, `Kill`, `ExitCode`) | `GitLoom.Core/Agents/PtyProcessShim.cs` (P2-03) |
| `IAgentWorktreeManager` (`RemoveAgentWorktree(force)`, `Prune`) + quarantine remotes | `GitLoom.Core/Agents/WorktreeManager.cs` (P2-06) |
| `SandboxEngine` (`docker pause/unpause/stop`), Docker-as-truth | `GitLoom.Core/Agents/Sandbox/SandboxEngine.cs` (P2-07) |
| Hardened git runner in the daemon; typed exceptions | P2-06 (F2 runner) |
| T-04 conflict resolver works against an arbitrary worktree path | `GitLoom.App` conflict-resolver stack (main, merged) |
| Dock/agent status surface consumes agent state enums | P2-13 (parallel; expose states via `AgentService` events regardless) |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/AgentLifecycle.cs` (`AgentContext : IDisposable`, state enum, teardown) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/YieldProtocol.cs` (IPC request/ready handshake + pause fallback) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/KeepAliveRebaser.cs` (yield → wip commit → rebase main → resume) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/GitMutationGuard.cs` (pure preconditions: mid-rebase / detached HEAD / index.lock backoff) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/SessionLeader.cs` + `LeaderRegistry.cs` (persistent PTY leader in the VM; daemon reattach) |
| **Edit** | `GitLoom.Server/Services/AgentGrpcService.cs` (spawn/stop wire the full lifecycle; events stream state changes) |
| **Create** | `GitLoom.Tests/YieldProtocolTests.cs`, `GitMutationGuardTests.cs`, `KeepAliveRebaserTests.cs`, `TeardownResidueTests.cs`, `LeaderReattachTests.cs` (Docker-tagged where needed) |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

1. **Cooperative Yield Protocol** — daemon sends `[IPC_UPDATE_REQUESTED]` on the agent's control
   channel, awaits `[IPC_UPDATE_READY]`; timeout → `docker pause`. **Only then** may the daemon
   touch the worktree. Every Git mutation is guarded: abort if the worktree is mid-rebase or on a
   detached HEAD; retry with exponential backoff on `index.lock`.
2. **Keep-alive rebase** — yield → `git add -A && git commit -m "wip: sync" && git rebase main` →
   resume. Conflicts → agent status `Conflict` + route to the T-04 resolver **against the
   worktree**.
3. **Session durability** — PTYs run under a persistent session leader inside the VM; a daemon
   restart reattaches (leader registry reconciled the same way P2-08 reconciles containers).
4. **Teardown** — `IDisposable` agent context: kill PTY, `git worktree remove --force`,
   `git branch -D agent/<id>`, close floating dock windows (client side reacts to the terminal
   event); filesystem verified clean.

---

## 3. Implementation steps

### 3.1 Control channel + `YieldProtocol`

- Control channel = a dedicated FIFO/second PTY channel into the container (not the interactive
  PTY — the marker strings must not race user-visible output). Simplest sound design: a named
  pipe mounted into the container that the adapter wrapper script watches; the wrapper prints
  `[IPC_UPDATE_READY]` to the pipe when the CLI is between tool calls. Fall back: if the adapter
  has no hook, the daemon treats the timeout path as normal (pause).
- `RequestYieldAsync(agentId, timeout)` → sends marker, awaits ready ≤ timeout (default 10 s),
  else `SandboxEngine.PauseAsync` and returns `Yielded.ByPause`. Always returns a token whose
  `Dispose/Resume` unpauses or signals resume — the **only** API through which mutation code
  reaches a worktree.

### 3.2 `GitMutationGuard` (pure)

`CanMutate(worktreeGitDirState) → Verdict` — checks for `rebase-merge`/`rebase-apply` dirs
(agent mid-rebase of its own — skip this cycle, retry next), detached HEAD, in-progress merge.
`RunGuarded(action)` wraps the runner call with `index.lock` detection → exponential backoff
(base 100 ms, factor 2, cap 5 tries) — the lock is transient by design here because the agent is
yielded/paused, so persistent lock = typed failure.

### 3.3 `KeepAliveRebaser`

Cycle (scheduled per agent, default every N minutes and after each human merge to main —
P2-10's `NotifyMainMoved` invokes it):

1. `RequestYieldAsync`.
2. Guard check (skip cycle on `rebase-merge` present — edge row 2).
3. Dirty? → `add -A` + `commit -m "wip: sync"`.
4. `git rebase main` (bare-repo-fetched `main` — the mirror's main, already current per P2-06).
5. Conflict → `git rebase --abort` is **not** automatic: status → `Conflict`, worktree left
   mid-rebase for the T-04 resolver against that path; PTY stays paused until resolved
   (resume-after-resolve hook).
6. Success → resume agent.

The human's live edits reach agent worktrees **only via Git** through this cycle — never file
sync (invariant 2).

### 3.4 `SessionLeader` + `LeaderRegistry`

- Leader = a tiny long-lived process inside the VM (outside the daemon lifetime) that owns the
  PTY file descriptors per agent; the daemon talks to it over a Unix socket
  (attach/detach/spawn/kill). `kill -9` the daemon → leader keeps PTYs alive; on boot the daemon
  reads the leader's registry (leader-owned state; **no daemon-side pidfiles**) and reattaches
  streams.
- Reconcile ordering on boot: containers (P2-08) → leaders → PTY reattach; mismatches resolved
  toward Docker truth (container dead ⇒ leader session reaped).

### 3.5 Teardown (`AgentContext.Dispose`)

Ordered, each step idempotent + failure-tolerant (continue and aggregate):
kill PTY (leader) → stop container (policy: stop vs keep persistent jail — teardown of the
*agent*, not the jail image) → `RemoveAgentWorktree(force:true)` → `git branch -D agent/<id>` in
the mirror → emit terminal event (client closes floating dock windows) → verify:
`git worktree list` clean, `docker ps -a` shows expected disposal. Verification is part of
Dispose (log + surface residue as a warning event, and fail tests on it).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| yield timeout | pause path exercised; mutation proceeds against a paused container |
| keep-alive while agent mid-`git rebase` of its own | guard skips (no mutation), retried next cycle |
| leader survives daemon `kill -9` | reattach test: PTY stream resumes, no output loss beyond the disconnect window |
| rebase conflict | status `Conflict`, worktree left for T-04 resolver, PTY paused until resolved |
| teardown residue | `git worktree list` + `docker ps -a` clean after Dispose |

---

## 5. Invariants (MUST)

1. The human's live edits reach agent worktrees only via Git (keep-alive rebase), never file sync.
2. **No Git mutation while the agent is unpaused/unyielded** — the yield token is the only
   mutation gateway (API-shape enforced: the runner wrapper for worktree paths requires the token).
3. Docker is truth for liveness (no `ps` polling — rejection trigger).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Yield_ReadyPath_RoundTrip` | scripted container answers ready → yield completes without pause |
| 2 | `Yield_Timeout_PausePath` | silent container → `docker pause` invoked, token resume unpauses |
| 3 | `Guard_MidRebase_Skips` | fixture worktree with `rebase-merge/` → verdict Skip, zero mutations |
| 4 | `Guard_IndexLock_BackoffThenTyped` | held lock released on 3rd try → succeeds; never released → typed failure after cap |
| 5 | `KeepAlive_CleanRebase_ResumesAgent` | dirty worktree + advanced main → wip commit + rebase + resume |
| 6 | `KeepAlive_Conflict_RoutesToResolver` | conflicting change → status `Conflict`, `rebase-merge` present, resume deferred |
| 7 | `Teardown_NoResidue` | Dispose → no worktree, no branch, container per policy, event emitted |
| 8 | `Leader_ReattachAfterDaemonKill` (`RequiresDocker`) | kill -9 daemon process in test harness → new daemon instance reattaches, echo works |

Scripted-container tests use a minimal image with a shell script standing in for an agent CLI.

---

## 7. Rejection triggers / Reviewer script

**Rejection:** touching a worktree without a completed yield; polling `ps` for agent liveness;
auto-aborting conflicted rebases; a second rebase driver.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Yield|FullyQualifiedName~GitMutationGuard|FullyQualifiedName~KeepAlive|FullyQualifiedName~Teardown"
grep -rn "Process.GetProcesses\|pgrep\|ps aux" GitLoom.Core/Agents/   # 0 hits
grep -rn "rebase --abort" GitLoom.Core/Agents/Orchestrator/KeepAliveRebaser.cs  # 0 automatic aborts
```

---

## 8. Definition of done

- [ ] Yield protocol (ready + pause paths) as the sole mutation gateway; guard + lock backoff pure-tested.
- [ ] Keep-alive rebase cycle with conflict → T-04 handoff; wired for P2-10's `NotifyMainMoved`.
- [ ] Session leader + reattach; teardown with residue verification.
- [ ] All edge rows tested; `AGENTS.md` Repository Map updated. One task = one PR linking **P2-09**, base `phase2`.
