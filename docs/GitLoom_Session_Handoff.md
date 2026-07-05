# GitLoom — Implementation Session Handoff

This document is the entry point for continuing the feature-implementation effort in a
fresh conversation. Read this first, then `AGENTS.md`, then the two planning docs below.

_Last updated: 2026-07-06 (end of the T-04 conflict-resolver session)._

---

## 0. The overarching task

Implement every feature in **`docs/GitLoom_Master_Implementation_Document.md`** one by one,
in build order (**T-02 → T-22**, plus **T-23** = direct PR/MR integration, added this effort).
Use **`docs/GitLoom_Feature_Plan_Triage.md`** to decide which tasks need **manual
verification**:

- **No manual verification needed** → implement, verify with build/tests (and the headless
  render harness for UI), commit, push, and move on without stopping.
- **Manual verification required** → implement, then stop and tell the user exactly how to
  manually verify.
- **Uncertain** → decide yourself if it's clear; otherwise ask.

Each task has a detailed plan doc under **`docs/feature-plans/T-0X-*.md`** and a matching
branch (originally `plan/T-0X-*`).

### Workflow rules (confirmed by the user — keep following these)

1. **Commit + push each finished task yourself** (the user delegated this; it overrides the
   "don't commit/push" line in `CLAUDE.md`).
2. **Stack dependent branches**: a dependent task branches off / merges its prerequisites.
3. **Rename `plan/` → `feature/`** for a task's branch, both locally and on the remote, when
   you start working it. (T-02/T-03/T-04 are already `feature/…`; T-05+ are still `plan/…`.)
4. Follow **`AGENTS.md`** (design system, Repository Map upkeep, git hygiene, invariants) —
   it is the source of truth and is kept current with the code.

---

## 1. What is DONE (this session): conflict resolution — T-02, T-03, T-04

The full conflict-resolution feature is implemented, built, and tested. It spans three
planned tasks that form one diamond stack (T-02 ⟂ T-03 are independent siblings off `main`;
**T-04 merges both**, so the `feature/T-04-conflict-resolver-ui` branch contains everything):

- **T-02 — merge chunker (engine):** `IMergeDiffService`/`MergeDiffService`
  (`GenerateMergeChunks`, `AssembleMerged`), `MergeChunk` model. Pure, IO-free, unit-tested.
- **T-03 — conflict index plumbing (service):** `GetConflicts`, `GetConflictBlobs`,
  `ResolveConflict`, `ResolveFileWithSide`, `RemoveFileFromMerge`, `HasUnresolvedConflicts`,
  `GetCurrentOperation`, `AbortMerge` — all via `IGitService.ExecuteWithRepo`. `ConflictedFile`
  model. Integration-tested against real conflicted repos.
- **T-04 — resolver UI:** a synchronized **IntelliJ-style 3-pane merge editor**
  (Ours | Result | Theirs) rebuilt from scratch on the T-02 engine. Per-side
  accept/reject/undo; live Result; stacked add/add slots with **flow-down connectors**;
  red = modify/modify, grey = add/add, green = accepted side + Result; equal, side-hugging
  accept/reject glyphs; word-level intra-line diff highlight.

**How the UI was verified without a display:** the headless render harness
`GitLoom.Tests/Headless/ResolverRenderHarness.cs` (`[AvaloniaFact]`, Skia headless) opens the
real resolver against a real 2-conflict repo and saves PNGs to `artifacts_headless/`
(gitignored). Read those PNGs to inspect the UI. `TestAppBuilder.cs` wires
`UseSkia().UseHeadless(UseHeadlessDrawing=false)`.

### ⚠️ T-04 deferred UI polish — come back to perfect it

The resolver is fully functional and matches the JetBrains reference on behavior and color,
but **one fidelity item is intentionally deferred** (documented in
`docs/feature-plans/T-04-conflict-resolver-ui.md` §12):

> The accept/reject **gutters should be overlays embedded on top of the code columns** (code
> text scrolling *underneath* a continuous highlight), as in the reference — rather than the
> current dedicated fixed-width `MergeGutter` column (`*,52,*,52,*` grid). True overlay +
> horizontal scroll pass-through in AvaloniaEdit is a deeper change left for a later pass.
> Also nice-to-have: base-line hint on unresolved modify rows; a word-diff "Show Details"
> toggle.

**Return to this UI-polish pass** once the higher-priority build-order tasks land. It does
not block T-04 acceptance.

---

## 2. How the DONE work is being landed (PR status)

`main` is governed by a **repository ruleset** (`main-protection`, active), which requires:

- **1 approving code-owner review** (dismiss-stale-on-push on),
- passing **required status checks**: `Build & Test` and `Format check` (strict / up-to-date),
- **linear history** — no merge commits; **squash or rebase merge only**.

Because `main` enforces review + green CI, **the merge itself is the user's to make** — do not
`--admin`-bypass the ruleset.

**Landing decision:** Since `feature/T-04-conflict-resolver-ui` already contains all of
T-02 + T-03 + T-04, and `main` squashes to a linear history, separate stacked PRs for T-02/T-03
add no value (they'd have to be rebased away and their diffs re-appear under T-04). So the whole
conflict-resolution feature lands through **one PR: `feature/T-04-conflict-resolver-ui → main`**.
The individual T-02/T-03 branches remain as history.

> If the user later wants per-task commits preserved on `main`, use **rebase-merge** on the
> T-04 PR (replays the task-level commits linearly); otherwise **squash** for one tidy commit.

CI note: `Build & Test` runs on **Linux/Release** and includes the headless render harness — it
passed locally on Windows; watch the first CI run in case Skia/font differences need attention.
If `Format check` fails, run `dotnet format` once at the repo root, commit, and push.

---

## 3. What's NEXT (resume here)

Per `docs/GitLoom_Feature_Plan_Triage.md`, the next independent, **auto-verifiable** tasks are:

1. **T-05 — tag management** (`plan/T-05-tag-management`)
2. **T-06 — partial / hunk staging** (`plan/T-06-partial-staging-ui`)
3. **T-07 — worktree porcelain** (`plan/T-07-worktree-porcelain`)

Implement these three straight through (rename `plan/`→`feature/`, build, test, commit, push,
open a PR each). Then:

- **T-08 — interactive rebase** depends on T-04 being verified/merged first — **pause** before it.
- Continue **T-09 … T-22** per the triage doc.
- **Host/credential-gated, deferred** (revisit when the user provides credentials/host setup):
  **T-14** (multihost auth/SSH), **T-15** (commit signing), **T-17** (LFS), **T-23** (PR/MR
  integration — spec added on `docs/pr-integration-plan`).

---

## 4. Environment & build (critical operational facts)

- **Build/test with the Windows SDK**, invoked from WSL:
  `"/mnt/c/Program Files/dotnet/dotnet.exe"` (10.0.300). There is no Linux `dotnet` and no
  Docker in this environment.
- Solution is **`GitLoom.slnx`** (a `.slnx`). Projects: **GitLoom.Core** (all logic, put logic
  here), **GitLoom.App** (thin Avalonia UI, `ViewModels/`↔`Views/` via `ViewLocator`),
  **GitLoom.Tests** (xUnit).
- **Build-lock gotcha:** `dotnet build` fails with **MSB3021 / MSB3027 "file locked by
  GitLoom.App (PID)"** if the app is running. That is a *lock, not a compile error* — the C#
  already compiled. Ask the user to **close the running app**, then rebuild. This recurred
  several times this session.
- Common commands:
  ```bash
  "/mnt/c/Program Files/dotnet/dotnet.exe" build GitLoom.Tests/GitLoom.Tests.csproj
  "/mnt/c/Program Files/dotnet/dotnet.exe" test  GitLoom.Tests/GitLoom.Tests.csproj --filter "FullyQualifiedName~ResolverRenderHarness" --no-build
  "/mnt/c/Program Files/dotnet/dotnet.exe" test  GitLoom.Tests/GitLoom.Tests.csproj --filter "FullyQualifiedName~MergeChunkViewModelTests" --no-build
  ```
- **Git remote actions use `gh`** (HTTPS/token; SSH fails). Remote:
  `github.com/dsazykin/GitLoom.git`.

---

## 5. Manual verification for T-04 (give this to the user)

1. Create a conflict: branch, edit the same lines two different ways on each side, merge → the
   resolver opens automatically (routed from `MergeConflictException`).
2. Colors: **red** where both sides edited the same existing line; **grey** where both added
   different new code at the same spot.
3. Click accept (`»`/`«`) on one side → that side **and the Result** go **green**, the other
   side keeps its conflict color, and the Result shows that side's text live.
4. Click the same accept again → it **undoes**. Reject (`✕`) both sides → the region empties.
   Accept both sides of an add/add → they **stack** (ours then theirs, theirs flows down).
5. `All Ours` / `All Theirs` bulk-resolve; `Mark Resolved` enables only when **every** conflict
   is resolved → click it and confirm the file leaves the conflicted list with merged content
   and (for a real merge) `git log` shows a 2-parent commit. Repeat with a rebase for the
   Continue-rebase path.

---

## 6. Key files touched this session (see `AGENTS.md` Repository Map for the full index)

- `GitLoom.Core/Models/`: `MergeChunk.cs`, `ConflictedFile.cs`, `ConflictSide.cs`
- `GitLoom.Core/Services/`: `IMergeDiffService.cs`, `MergeDiffService.cs`, `GitServices.cs`,
  `IGitService.cs`
- `GitLoom.App/ViewModels/`: `ConflictResolverWindowViewModel.cs` (+ `MergeChunkViewModel`,
  `SideChoice`), `ConflictedFilesViewModel.cs`
- `GitLoom.App/Views/`: `ConflictResolverWindow.axaml` + `.axaml.cs` (the 3-pane merge editor,
  `MergeGutter`, `MergeBandRenderer`), `ConflictedFilesWindow.axaml`
- `GitLoom.Tests/`: `MergeDiffServiceTests.cs`, `GitServiceConflictTests.cs`,
  `MergeChunkViewModelTests.cs`, `Headless/TestAppBuilder.cs`, `Headless/ResolverRenderHarness.cs`

Untracked reference screenshots in the repo root (`resolver.png`, `reference_merge_window.png`,
`conflict_viewer.png`, `our_resolver.png`) are the user's — leave them; `artifacts_headless/`
is gitignored.
