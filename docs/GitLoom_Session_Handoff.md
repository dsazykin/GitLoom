# GitLoom — Implementation Session Handoff

This document is the entry point for continuing the feature-implementation effort in a
fresh conversation. Read this first, then `AGENTS.md`, then the planning docs in §0.

_Last updated: 2026-07-06 (end of the T-05 / T-06 / T-07 session)._

---

## 0. The overarching task

Implement every feature in **`docs/GitLoom_Master_Implementation_Document.md`** one by one,
in build order (**T-02 → T-22**, plus **T-23** = direct PR/MR integration). Each task has:

- a **contract / edge-case matrix / invariants** in the Master Doc (binding),
- a matching **test contract** `TI-NN` in **`docs/GitLoom_Test_Implementation_Strategy.md`**,
- a detailed **plan doc** that lives on the task's `plan/T-0X-*` branch at
  `docs/feature-plans/T-0X-*.md` (only merged tasks' plan docs are on `main`), and
- a **manual-verification triage** in **`docs/GitLoom_Feature_Plan_Triage.md`** (may be a
  local/untracked file — the key conclusions are reproduced in §5 below so they survive).

---

## 1. WORKFLOW RULES (confirmed by the user this session — these OVERRIDE the old ones)

1. **Branch each task off `origin/main`.** `origin/main` already contains T-02…T-06. Create
   `feature/T-0X-*` from `origin/main`, then `git checkout plan/T-0X-* -- docs/feature-plans/T-0X-*.md`
   to carry that task's plan doc onto the branch (as T-05/06/07 did).
2. **Commit AND push each finished task yourself** — but **DO NOT open the PR.** The user opens
   PRs themselves on GitHub. (This changed mid-session: the old handoff said "open a PR each"; it
   no longer applies.) End commit messages with the `Co-Authored-By:` + `Claude-Session:` trailers.
3. **Merge `origin/main` into your working branch** — the current one and every new branch —
   right after branching and again before finalizing: `git fetch origin && git merge origin/main
   --no-edit`. The user lands PRs continuously, so `origin/main` moves; keep branches synced.
   Resolve conflicts (usually just the AGENTS.md Repository Map — combine both sides).
4. **Come back to the user for anything not FULLY self-verifiable.** The headless render harness
   + present tooling make most tasks auto-verifiable, but for the interaction/animation weak spots
   and external-account tasks (see §5), implement the verifiable parts, then **pause and hand the
   user a manual checklist** rather than self-signing-off.
5. Follow **`AGENTS.md`** (design system, Repository Map upkeep in the SAME change, invariants,
   no raw colors, `type: summary` commits). It is the source of truth and is kept current.

---

## 2. What is DONE and MERGED (or pushed)

`origin/main` currently contains, in order:

| Task | Feature | State |
|---|---|---|
| **T-02 / T-03 / T-04** | Conflict resolution: pure 3-way merge chunker, conflict-index plumbing, and the synchronized 3-pane resolver UI | Merged (PR #25) |
| **T-05** | Tag management: `GitTagItem`; `GetTags/CreateTag/DeleteTag/PushTag/DeleteRemoteTag/CheckoutTag`; Tags section in the branch browser; `CreateTagDialog`; graph tag chips | Merged (PR #26) |
| **T-06** | Partial staging: pure `PatchParser`/`PatchBuilder` engine + diff-viewer UI (per-hunk stage/unstage/discard, unified-view drag-select of lines, side-by-side resolver-style block accept/discard) | Merged (PR #27) |
| **T-07** | Worktree porcelain: `WorktreeItem` + pure `WorktreePorcelainParser`; `ListWorktrees` (→`IReadOnlyList<WorktreeItem>`), `AddWorktree(+createBranch)`, `RemoveWorktree(+force)`, `PruneWorktrees`; whole-tree `GetDiffAgainstCommit` + "Diff working tree against this commit" menu | **Pushed** on `feature/T-07-worktree-porcelain`; PR is the user's to open |

**Full suite is green: 207 tests** (on the T-07 branch, which has `origin/main` merged in).

### T-04 deferred UI polish (still open, non-blocking)
The resolver's accept/reject **gutters** should become overlays embedded on the code columns
(code scrolling under a continuous highlight) rather than the dedicated fixed-width gutter; plus a
base-line hint on unresolved modify rows and a word-diff "Show Details" toggle. See
`docs/feature-plans/T-04-conflict-resolver-ui.md` §12. Revisit when convenient.

---

## 3. What's NEXT (resume here)

**T-08 — interactive rebase.** Its dependencies (T-04, T-07) are now satisfied (T-04 merged, T-07
pushed). Triage rates it **"Yes / auto-verifiable"** (mostly test-backfill on
`InteractiveRebaseService`, which already exists; the UI is verify-only tightening). Its plan doc is
on `plan/T-08-interactive-rebase`. Note the `Program.cs` argv modes (`--rebase-editor`/`--rebase-msg`)
that already exist — git launches the app as its own sequence/commit editor; don't reorder that parse.

Then continue **T-09 … T-22** per the Master Doc build order (§3 of that doc is authoritative for
per-task dependencies). **Skip on reach** (blocked on external accounts/tooling): **T-14** (multi-host
OAuth/SSH), **T-23** (PR/MR integration) — build their offline slices only, defer the live matrix.

---

## 4. Environment & build (critical operational facts)

- **Build/test with the Windows SDK invoked from WSL:** `"/mnt/c/Program Files/dotnet/dotnet.exe"`.
  There is no Linux `dotnet` and no Docker here.
  ```bash
  "/mnt/c/Program Files/dotnet/dotnet.exe" build GitLoom.slnx -v q -clp:ErrorsOnly
  "/mnt/c/Program Files/dotnet/dotnet.exe" test  GitLoom.Tests/GitLoom.Tests.csproj
  "/mnt/c/Program Files/dotnet/dotnet.exe" format GitLoom.slnx --verbosity minimal   # CI has a required Format check
  "/mnt/c/Program Files/dotnet/dotnet.exe" test  GitLoom.Tests/GitLoom.Tests.csproj --filter "FullyQualifiedName~Worktree"
  ```
- **Tests run under Windows git/gpg/git-lfs** (Git-for-Windows bundles all three: git 2.53, gpg 2.4.8,
  git-lfs 3.5.1). So `RequiresGitCli`, `RequiresGpg`, and `RequiresGitLfs` suites all **run here** (they'd
  skip on a machine lacking the tool — flag any green that depends on gpg/lfs as "verified *here*").
- **Build-lock gotcha:** `dotnet build` fails with **MSB3021/MSB3027 "file locked by GitLoom.App (PID)"**
  if the app is running. That's a *lock, not a compile error* — ask the user to close the app, then rebuild.
- **Headless render harness** (`GitLoom.Tests/Headless/`, `[AvaloniaFact]` + Skia): drives real Views
  against real fixture repos and saves PNGs to `artifacts_headless/` (gitignored) — **read the PNGs** to
  inspect UI. It also **injects pointer input** (see `PartialStagingRenderHarness` drag-select test), so
  interactions can be exercised, not just rendered. Harnesses: `ResolverRenderHarness` (conflict resolver),
  `TagUiRenderHarness` (tags), `PartialStagingRenderHarness` (partial staging).
- **`main` is protected** (ruleset `main-protection`): 1 code-owner review, required checks
  `Build & Test` + `Format check`, **linear history** (squash/rebase merge only). The user merges.
- **Git remote actions use `gh` / HTTPS token** (SSH fails). Remote: `github.com/dsazykin/GitLoom.git`.

---

## 5. Self-verification triage map (which upcoming tasks need a "come back")

Produced this session from `GitLoom_Feature_Plan_Triage.md` + the headless harness + present tooling.
**Fully self-verifiable (proceed autonomously):** T-08, T-10, T-12, T-16, T-18, T-19 (sweeping — check
every mutating op is covered), T-20, T-22, and **T-15 / T-17 locally** (gpg + git-lfs present).
**Come back to the user** (implement the verifiable slice, then hand a manual checklist):
- **T-09** — graph interactions: hit-tester is pure/testable; the **drag-to-rebase** gesture needs a human pass.
- **T-11** — blame: service/cache testable; the AvaloniaEdit blame-gutter **rendering** wants a look.
- **T-13** — diff quality: intra-line/whitespace pinned; the **image-diff swipe** control needs a human pass.
- **T-21** — profiles/clone: profile-apply + cancel-delete testable; the **live progress animation** wants a look.
- **T-14, T-23** — external accounts (real GitHub/GitLab OAuth/PAT/SSH; PR create/list/merge). Skip on reach.

The interaction weak spots (drag/swipe/animation) are exactly what the harness can render and even
drive, but can't judge for *feel* — hence the come-back.

---

## 6. Persistent memories in play (auto-loaded each session)

- **Merge origin/main into working branches** — keep feature branches synced with remote `main`.
- **Come back on not-fully-verifiable triage tasks** — pause for a human pass (see §5).
- **Use `gh` for git remote actions** — SSH fails; HTTPS/token via gh.

---

## 7. Key files added across the conflict-resolution → worktree sessions

(See the `AGENTS.md` Repository Map for the authoritative index.)

- **Core models:** `MergeChunk`, `ConflictedFile`, `ConflictSide`, `GitTagItem`,
  `DiffLine`/`DiffHunk`/`FilePatch` (`DiffHunk.cs`), `WorktreeItem`.
- **Core services:** `IMergeDiffService`/`MergeDiffService`, `PatchParser`, `PatchBuilder`,
  `WorktreePorcelainParser`, plus the tag/conflict/worktree methods on `IGitService`/`GitServices`.
- **App:** `ConflictResolverWindow`/`ConflictedFilesWindow` + VMs; `CreateTagDialog` + VM;
  `DiffViewerViewModel` partial-staging (+ `DiffHunkRowViewModel`/`DiffLineRowViewModel`).
- **Tests:** `MergeDiffServiceTests`, `GitServiceConflictTests`, `MergeChunkViewModelTests`,
  `GitServiceTagTests`, `PatchParserTests`, `PatchBuilderTests`, `GitServicePartialStagingTests`,
  `WorktreePorcelainParserTests`, `GitServiceWorktreeTests`, and the three `Headless/*RenderHarness`.
  Real-git patch corpus in `GitLoom.Tests/TestData/patches/` (LF-locked via `.gitattributes`).
