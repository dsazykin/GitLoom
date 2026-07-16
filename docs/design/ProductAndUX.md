# Mainguard Product & UX Depth — Lane I

**Is the product smart and delightful to USE?** Product management + UX research + interaction design for the shipped Git client. This document is the sibling of the *visual* design corpus (`DesignSystem.md`, `SurfaceDesigns.md`, `FeatureDesigns.md`) and asks the questions those don't: are we building the right next things (Part 1), are the shipped flows actually usable (Part 2), is every action reachable and learnable (Part 3), and do the analytics tell the truth beautifully (Part 4).

**Sources of truth.** DESIGN.md and PRODUCT.md govern the system and register; the Voice & Delight Bible (`docs/creative/GitLoom_Voice_And_Delight_Bible.md`) governs every string quoted here (cited by rule id, e.g. **V-4**, **C-1**); AGENTS.md pins every claim to a real view, ViewModel, service, or token. Nothing here invents a color, radius, spacing value, or motion outside the system. Where this document proposes copy, it follows the Bible's five-question gate (Appendix A there).

**Scope fence.** Everything in Parts 1–4 serves the shipped single-user client (per Design Principle 5 — design for today's tool, architect for tomorrow's swarm). Where a feature is also a seam the phase-2 platform will reuse, that is stated as an architecture note, never as UI to build now.

_Last updated: 2026-07-11._

---

## Part 1 — Feature ideation: the delight backlog beyond C1–C5

The client-parity set (P2-C1…C5: bisect, global search, the Repositories home, split-into-branches, and the polish pack — mergetool/difftool/partial stash/patches/templates/diff search/AI draft) is already designed end-to-end in `FeatureDesigns.md`. This part is what comes *after* that set — net-new features that would make a developer choose Mainguard on feel, not feature-count. Ten candidates, three tiers.

**What "delight" means here** (so the priorities aren't taste-by-assertion): Mainguard's core promise is *you never lose work and you always know what's true* (PRODUCT.md Design Principle 4, Bible V-5/V-6). A feature earns Tier 1 when it converts existing engineering (the T-19 journal, the T-07/T-29 worktree backend, the pure merge/patch engines) into *daily-felt* safety or speed — high leverage, low new-machinery. Novelty for its own sake is a non-goal.

### Tier 1 — build next (converts shipped engineering into daily-felt delight)

#### I-1 · The Shelf becomes a place, not a list — richer stash management

**Job to be done.** *"When I'm interrupted mid-change, I want to park my work and later trust that I can find, inspect, and re-apply exactly what I parked — without re-deriving what it was from a one-line message."*

**Today, verified.** The Shelf tab in `StagingPanelView.axaml` (lines 229–254) is a flat list of `GitStashItem` rows with Pop / Apply / Drop buttons. There is no way to see *what is inside a stash* before applying it; `StashDrop` executes with no confirmation and is non-undoable (T-19 flags stash drops as journaled-but-not-undoable); a failed `StashPop`/`StashApply` writes its exception to `Console.WriteLine` and the UI shows success-shaped silence (`StagingPanelViewModel.cs:634–666` — Part 2 findings F-A7/F-A8). The stash is currently the *least* safe corner of an app whose brand is safety.

**Sketch.**
- **Detail pane:** selecting a Shelf row opens a read-only diff of the stash against its parent (Core: a `GetStashDiff(repoPath, index)` on `IGitService` via `ExecuteWithRepo` — the same read-only patch rendering `FileHistoryView` already uses), plus provenance metadata the stash already carries: the branch it was taken on, age, file count. A stash older than ~14 days gets a quiet `TextMuted` age note (**TT-1**), never an alarm.
- **Apply to a new branch:** a per-row "Restore to new branch…" (CLI `git stash branch <name>` through the audited `RunGitChecked` path) — the correct escape when the original branch has moved on and a plain pop would conflict.
- **Confirmed, honest Drop:** route Drop through `IConfirmationService` with the **C** pattern: `Drop shelf "wip: header refactor"? Its 4 changed files are discarded. A dropped shelf is not undoable.` — `Button.Danger` "Drop", per **C-1/C-2/C-3**.
- **Pop lands somewhere:** a pop/apply that conflicts routes into the existing `ConflictedFilesWindow`/`ConflictResolverWindow` flow instead of dying in the console; a pop that succeeds gets the standard **T-1** toast.

**Priority rationale.** Highest ratio in the backlog: two of the three severity-1 findings in Part 2 live here, every ingredient exists (diff rendering, resolver, confirmation service, journal), and stashing is a many-times-daily verb. This is the feature where fixing bugs and adding delight are the same work. *(Partial stash — choosing hunks at shelf time — is C5 item 3 and stays there; I-1 is the browse/inspect/recover layer above it.)*

#### I-2 · Conflict forecast — know before you merge

**Job to be done.** *"Before I start a merge or rebase, I want to know how bad it will be, so I can choose merge vs rebase vs postpone — instead of starting, hitting a wall of conflicts, and aborting in fear."*

**Sketch.**
- **Core:** a `ForecastMerge(repoPath, ours, theirs)` on `IGitService` that performs an **in-memory** merge via `ExecuteWithRepo` (LibGit2Sharp's `ObjectDatabase.MergeCommits` merges trees without touching the working directory or index) and returns the would-be `ConflictedFile` list. Read-only, index.lock-safe, cancellable.
- **UI:** one line of fact in the surfaces where the decision is made — `MergeCommitDialog`, and the T-09b drag-to-rebase flyout's two options: `Rebase feature onto main — no conflicts expected` / `Merge feature into main — 3 files will conflict`, `TextMuted`, computed async with a quiet indeterminate state while it runs (**M-6**: honest or absent — never a guess presented as fact, per **V-6**; if the forecast can't complete, say nothing rather than something stale).
- Clicking the conflict count previews the file list (the same row template `ConflictedFilesWindow` uses), read-only.

**Priority rationale.** This is the product thesis — "the exact bug this app exists to prevent" generalized from lock files to dread. It converts the scariest moment in git into information, costs no new engine (the merge machinery exists), and is the visible ancestor of the phase-2 safe-to-merge verification story. Cheap, on-brand, differentiating: no mainstream GUI does this.

#### I-3 · Undo made ambient — the journal leaves its drawer

**Job to be done.** *"When I do something wrong in git, I want to press the same undo I press everywhere else — not reconstruct state from the reflog or find a history dialog."*

**Today, verified.** T-19 built the hard part: `OperationJournal` snapshots every mutating operation, `Undo`/`Redo` restore refs with a dirty-tree guard (`UndoBlockedException`), non-undoable ops are journaled with a reason. But the *experience* is `OperationHistoryWindow`, reachable only from the branch-pill flyout ("Operation History…", `MainWindow.axaml:192`). The app's single strongest safety fact is invisible at the moment of panic.

**Sketch.**
- **Gestures:** `Ctrl+Z` / `Ctrl+Shift+Z` bound to journal undo/redo whenever focus is not in a text editor (the scoping rule Part 3 §3.5 defines). Undo first shows a **peek**, not an action: a confirmation naming the exact operation — `Undo "Hard reset main to a1b2c3d"? Your branch returns to f9e8d7c.` (**C-1/C-5**) — because git undo is rarer and heavier than text undo, and a silent one would itself be frightening.
- **Ambient affordance:** the last journaled operation as a one-line `TextMuted` entry at the foot of the branch-pill flyout (`Undo: hard reset · 2 min ago`), so the way back is visible before anyone searches for it (**V-5** made structural).
- **Blocked undo** surfaces the existing typed reason verbatim from Microcopy pattern usage: `Can't undo this yet — undoing would overwrite uncommitted changes in 3 files. Commit or stash them first, then undo.` (**V-5** exemplar, already the Bible's own example).

**Priority rationale.** Zero new Core machinery — this is pure interaction design over shipped, tested code, and it is the single highest-trust gesture the app can offer. It also *compounds*: every confirmation dialog that can end with "…and this is undoable from Ctrl+Z" (per **C-5**) gets calmer.

### Tier 2 — strong candidates, sequenced after Tier 1

#### I-4 · Worktree-first branch switching — never stash to look

**Job to be done.** *"When a review or hotfix interrupts me, I want to open that other branch beside my work, not on top of it."*

**Sketch.** The backend is fully shipped and idle: `CheckoutBranchWorktree` (T-29) creates a worktree for any ref, `WorktreeWindow` (T-21) manages them. Elevate to a flow: (a) **"Open in a worktree…"** on every branch context menu and on the `CheckoutConflictDialog` — when a checkout is blocked by dirty state, the safer third path (**C-4**) becomes *"Open `release/1.2` in a worktree instead — your changes here stay untouched"*; (b) a `TextBlock.Mono` worktree chip in the title-bar area when the open repo *is* a linked worktree, naming the main checkout (E4: chips carry text); (c) `WorktreeWindow` rows gain dirty/clean state so a worktree is never removed blind.

**Priority rationale.** Worktrees are the phase-2 substrate (every agent lives in one); teaching the human the same mental model now is a strategic two-for-one. Sequenced behind Tier 1 because the interrupted-work JTBD is partially served today by stash+I-1.

#### I-5 · Interactive rebase, graph-native

**Job to be done.** *"When I clean up a branch, I want to see what history I'm about to create — not simulate a todo list in my head."*

**Sketch.** Four increments over `InteractiveRebaseWindow` / `InteractiveRebaseViewModel`:
1. **Drag to reorder** rows (the `LabelDragGesture` pattern from T-09b, ~5px arm threshold so click still selects), replacing the 10px ▲▼ buttons as the primary gesture (they remain for accessibility).
2. **Reorder forecast:** a pure, testable overlap check — two commits whose patches touch overlapping hunk ranges (via the existing `PatchParser`) get a quiet `WarningBrush`-dotted note *"may conflict if reordered"* on the dragged row. Heuristic and labeled as such (**V-6**: never claim certainty the tool lacks — "may", not "will").
3. **Autosquash pre-grouping:** on open, `fixup!`/`squash!` subjects are pre-assigned their action and folded under their targets (the accent fold rail at `InteractiveRebaseWindow.axaml:33` already draws the grouping).
4. **"Modify this commit…"** on the graph context menu: seeds a one-commit `edit` plan — the graph is where the intent forms (SurfaceDesigns §2's thesis).

**Priority rationale.** Rebase is the highest-stakes flow with the largest gap between Mainguard's plumbing (solid) and its interaction depth (a modal list). Each increment is independently shippable; forecast (2) shares its honesty pattern with I-2.

#### I-6 · Resolution memory — rerere, surfaced honestly

**Job to be done.** *"When I hit the same conflict twice (long-lived branch, repeated rebases), I don't want to solve it twice."*

**Sketch.** An opt-in preference ("Remember conflict resolutions", `UserPreferences`, JSON) that sets `rerere.enabled` in **local** repo config (the `ApplySigningConfig` precedent — never global). When a recorded resolution applies, the resolver's affected chunk opens **pre-filled but not pre-accepted**: the `MergeChunkViewModel` band carries a labeled chip *"Re-applied your resolution from Jun 30 — review, then accept"* (**E4** text-anchored, **V-6** provenance). Accepting is still the human's act; the delight is arriving to find the work already staged for review.

**Priority rationale.** The second rung of the smart-conflict ladder (I-2 is the first). Sequenced later because it only pays off in repos with recurring conflicts — high ceiling, narrower audience.

#### I-7 · Branch sweep — the merged-branch janitor

**Job to be done.** *"My branch list is full of landed work; I want to clear it in one confident pass, not one fearful delete at a time."*

**Sketch.** A "Sweep branches…" entry in the branch browser: Core computes, per local branch, *merged into the default branch* (ancestry via `ExecuteWithRepo`) or *upstream gone* (tracking ref deleted). A pre-checked review list (unmerged branches are listed too, unchecked and labeled why), then one confirmed batch delete — every deletion journaled, and branch-delete undo already **recreates upstream config** (T-19), so the dialog can truthfully close with *"Deleted 7 branches. Each is restorable from Operation history."* (**C-5**).

**Priority rationale.** Small, self-contained, and the rare cleanup feature that can promise full reversibility because the journal already guarantees it. A natural first-week delight moment.

#### I-8 · Safety snapshots — the invisible net under destructive ops

**Job to be done.** *"When I confirm a hard reset or a bulk discard, I want the tool to have kept a copy anyway — because sometimes the confirmation was the mistake."*

**Sketch.** Before a hard reset, forced checkout, or a discard touching more than ~5 files, Core runs `git stash create` (writes a dangling stash commit **without touching the worktree or stash list**), records its SHA in the operation's `JournalEntry`, and the confirmation dialog's recoverability line becomes concrete: *"Your working tree is replaced to match. Mainguard keeps a snapshot of the discarded state, recoverable from Operation history for 30 days."* Snapshot SHAs older than the window are simply forgotten (git GC handles the rest — no cleanup daemon).

**Priority rationale.** Turns **C-2** ("body names recoverability explicitly") from careful wording into a mechanical guarantee. Sequenced in Tier 2 because Tier 1's I-3 must land first — a snapshot nobody can reach is not a net.

### Tier 3 — worth holding, not next

#### I-9 · Pickaxe — "where did this string come from / go?"

**Job to be done.** *"I'm staring at a line and need the commit that introduced or removed this exact text — blame only shows the last touch."*

**Sketch.** From a text selection in `DiffViewerView` or `BlameView`, a context entry "Trace this text through history" runs `git log -S<text> --format=…` through the audited CLI path and feeds the results into the existing `FileHistoryView` revision list (it already renders revision→diff). Pairs naturally with the C2 global-search overlay as a query mode later.

**Priority rationale.** Genuinely loved by the people who know it exists — but it's an archaeology tool, weekly not daily. It rides on `FileHistoryView`, so cost stays low whenever it's picked up.

#### I-10 · "While you were away" — one line, not a dashboard

**Job to be done.** *"When I open a repo after a weekend, I want the delta since I left — without reading the graph cold."*

**Sketch.** On workspace open, if the last open was >8h ago: **one dismissible hairline bar** above the timeline — `Since Friday: 12 commits on origin/main · release/1.2 updated · your shelf "wip: header" is 14 days old.` Facts with counts (**V-1**), newest sources first, one line, `TextMuted`, no cards, no metrics, no sparkline — this is explicitly *not* the enterprise hero-metric scaffold PRODUCT.md bans; the moment it grows a second line it has failed its own gate. Data comes from reads that already exist (ahead/behind, `AutoFetchService.GetLastFetched`, `GetStashes`).

**Priority rationale.** High charm, but it's an *orientation* aid whose heavy lifting (multi-repo attention) is already C3's Needs-attention lane. Kept deliberately last so it never grows into a dashboard.

### Priority ledger

| # | Feature | Tier | Leverage (existing seam) | New Core surface | Risk |
|---|---|---|---|---|---|
| I-1 | Shelf elevated | 1 | `FileHistoryView` diff path, resolver, `IConfirmationService` | `GetStashDiff`, `StashToBranch` | Low |
| I-2 | Conflict forecast | 1 | `ObjectDatabase.MergeCommits`, `ConflictedFile` | `ForecastMerge` | Low (read-only) |
| I-3 | Ambient undo | 1 | `OperationJournal` (whole of T-19) | none | Low |
| I-4 | Worktree-first switching | 2 | `CheckoutBranchWorktree` (T-29), `WorktreeWindow` | dirty-state read | Low |
| I-5 | Rebase, graph-native | 2 | `LabelDragGesture`, `PatchParser`, fold rail | reorder-overlap check | Medium |
| I-6 | Resolution memory | 2 | local-config pattern (T-15), `MergeChunkViewModel` | rerere read/enable | Medium |
| I-7 | Branch sweep | 2 | journal branch-delete undo | merged/gone computation | Low |
| I-8 | Safety snapshots | 2 | `JournalEntry`, confirmation dialogs | `git stash create` wrap | Low |
| I-9 | Pickaxe | 3 | `FileHistoryView`, CLI path | `log -S` wrap | Low |
| I-10 | Away digest | 3 | ahead/behind, auto-fetch, stashes | none | Scope-creep |

---

## Part 2 — Usability heuristic audit of the shipped client

**Method.** Nielsen's ten heuristics applied through cognitive walkthroughs of the three core flows — *clone → commit → push*, *conflict resolution*, and *interactive rebase* — asking at each step: does the user know what to do, can they tell it worked, and do they ever fear losing work? Every finding is pinned to the shipped view/ViewModel it lives in (file and line where load-bearing). Findings the visual-design corpus already rules on are cross-referenced, not re-litigated.

**Severity scale.** **S1** — a user can lose work or be silently lied to; fix before anything else. **S2** — a real user hesitates, mis-models, or mistrusts; fix this release. **S3** — polish; batch with theme sweeps.

### 2.1 Flow A — clone → commit → push

**Walkthrough.** A new user lands on `CloneDashboardView`, authenticates, clones, edits files, opens the staging panel, commits, pushes. Where they hesitate:

**F-A1 · The front door is GitHub-shaped — S2** *(Match between system and real world / Flexibility).*
The unauthenticated `CloneDashboardView` hero is `Connect to GitHub` with a single accent CTA `Login with GitHub` (`CloneDashboardView.axaml:15–22`). A user with a GitLab remote, a plain URL, or a repo already on disk sees no door at all on this surface — the open-local and clone-by-URL paths live elsewhere. SurfaceDesigns §5 already designs the three-door first-run (open / clone / connect); this finding ratifies that design as a *usability* fix, not just a visual one. **Fix:** implement SurfaceDesigns §5.2's IA; until then, add the two secondary doors as `Button.Secondary` under the hero (**ES-2**: one accent, plural paths).

**F-A2 · The stash is the least safe corner of a safety product — S1** *(Error prevention / Visibility of system status).*
Three defects in one surface, all verified in `StagingPanelViewModel.cs`:
- `StashDrop` (line 662) executes immediately — no confirmation, and a dropped stash is **non-undoable** (T-19 journals it with a blocked reason). One mis-click on the Shelf row's `Drop` button (`StagingPanelView.axaml:246`) permanently discards parked work.
- `StashPop` and `StashApply` (lines 634–659) catch exceptions into `Console.WriteLine`. A pop that conflicts or fails shows the user *nothing* — the files sit unchanged and the UI is success-shaped silence. This is the exact "fear of losing work" moment: the user cannot tell whether their stash applied, half-applied, or vanished.
- The Drop button is styled `Classes="Secondary"` with a `DangerBrush` foreground override (`StagingPanelView.axaml:246`) instead of the `Button.Danger` role — severity riding a color override, not the component role (**V-2**, DESIGN.md §5).
**Fix:** the I-1 package (Part 1): confirmed Drop through `IConfirmationService` (**C-1/C-2/C-3**), pop/apply failures surfaced through the standard notification path with pattern-**E** copy, conflicted pops routed into the resolver.

**F-A3 · Deleting an untracked file is instant and forever — S1** *(Error prevention).*
`DeleteFileCommand` (`StagingPanelViewModel.cs:467–491`) deletes a file from disk with no confirmation. The directory guard at line 477 is excellent (a mis-clicked directory delete is refused with a named path — an exemplar of **V-1**), but a plain untracked *file* — often the newest, only-copy work in the repo — is removed with no dialog, no journal entry, no way back. Contrast: `RollbackFile` (line 510) correctly gates through `ConfirmDiscardAsync`. **Fix:** route file deletion through the same two-list confirmation (`Remove 1 untracked file from disk: … This cannot be undone.`), and fold it under I-8's snapshot net where applicable.

**F-A4 · Three names for discarding, two names for stashing — S2** *(Consistency and standards; Bible N-6: one term per concept).*
The same destructive concept is `Rollback` in the toolbar and context menu (`StagingPanelView.axaml:56`, `:102`), `Discard Changes` in the dialog title, and `Revert N tracked files` in the dialog body and confirm button (`StagingPanelViewModel.cs:549–565`) — and `Revert` collides with git's own `git revert`, which does something entirely different. Likewise the tab is `Shelf` (`StagingPanelView.axaml:42`) while every command, model, and journal entry says *stash* (`StashPushCommand`, `GitStashItem`). A user who learns one word cannot find it under the other. **Fix:** one term each — recommend **Discard** (matches `DiscardChanges`/`DiscardHunk` in Core and the Microcopy corpus) and pick **Shelf** *or* **stash** app-wide (N-6 requires the choice, not this doc; note the marketing/docs corpus says stash).

**F-A5 · The branch pill is secretly the app's junk drawer — S2** *(Recognition rather than recall / Match).*
The branch selector's flyout (`MainWindow.axaml:181–239`) contains eleven repo-management actions — Fetch, Update Project, Operation History…, Reflog…, Manage Remotes…, Submodules…, Worktrees…, Git Profiles…, Git LFS…, Accounts…, SSH Keys… — stacked *above* the branch list. Nothing about a branch pill suggests "SSH keys live here"; users find these by exhaustive search, once, and forget. Three sub-defects:
- **"Update Project"** (`:191`) is JetBrains dialect for *pull* — the palette registers `Pull` (`MainWindowViewModel.cs:411`), so the same operation has two names in two places (N-6).
- The flyout's search box (`:186`) has **no `Text` binding** — it renders, accepts focus and keystrokes, and filters nothing, while its watermark promises `Search for branches and actions`. A control that promises and does nothing teaches distrust of every other control (Visibility of status / trust).
- The management list makes the *branch switch* — the pill's actual job — scroll below the fold in a 230px viewport (`:204`).
**Fix:** management actions move to the command palette (Part 3 closes the coverage gap) and a proper repo menu; the pill flyout returns to branches + New branch + Fetch; the search box gets wired to filter branches (its watermark then says just `Search branches`) or is removed until it works.

**F-A6 · The second dead search box — S2** *(same class).* `CommitTimelineView.axaml:26`'s `Branch or tag` filter box is also unbound (no `Text` binding), sitting beside the working `Text or hash` search (`:58`, bound to `SearchText`). Same fix: wire or remove; never ship an inert affordance.

**F-A7 · A disabled Commit never says why — S2** *(Visibility / Help users recover; Bible TT-2).*
`CanCommit` requires a non-empty message **and** at least one checked file (`StagingPanelViewModel.cs:295`). Both are reasonable, but the disabled accent button carries no tooltip, so the "why is Commit greyed out?" moment — the single most common first-session stall in any Git GUI — has no answer on the surface. Worse: with **Amend last commit** checked (`StagingPanelView.axaml:177`), a message-only amend (reword) is impossible, because `CanCommit` still demands a checked file — a genuine capability gap, not just a hint gap. **Fix:** TT-2 tooltips on the disabled state (`Select at least one file to commit` / `Enter a commit message`); relax `CanCommit` when amending so a reword needs no file.

**F-A8 · Error toasts leak raw exception text — S2** *(Error recovery; Bible E-2: the library's terms never surface).*
`$"Rollback Failed: {ex.Message}"`, `$"Delete Failed: {ex.Message}"`, `$"Abort Rebase failed: {ex.Message}"` (`StagingPanelViewModel.cs:460, 489, 522, 619`) forward whatever LibGit2Sharp said, in Title Case, with no way back (**V-5**). The Microcopy corpus (§2) already wrote the pattern-**E** replacements — they are not yet wired here. **Fix:** map typed exceptions to the Microcopy strings; anything unmapped gets the generic **E** shape with the operation and object named.

**F-A9 · Quiet paper cuts on the commit path — S3.**
- Commit box watermark `Commit Message (Hit Enter for Description)` (`StagingPanelView.axaml:183`): Title Case, and "Hit Enter" describes a newline as if it were a mode switch. Per **V-8**/Microcopy: `Summary — press Enter to add a description`.
- `AddToGitignore` (`StagingPanelViewModel.cs:494–507`) appends without checking for duplicates or a trailing newline, and gives no confirmation toast (**T-1**: `Added src/temp.log to .gitignore.`).
- The inert `Exit` menu item (`MainWindow.axaml:165`) has no `Command` — it does nothing when clicked.
- Unversioned rows render filename *and* glyph in `DangerBrush` (`StagingPanelView.axaml:149–150`) — a new file is not a danger; already ruled by DesignSystem.md Part 2 audit row 5; noted here because in the *commit walkthrough* it reads as "something is wrong," which makes first-time users hesitate to commit at all.
- The `Reclone Repository?` confirmation (`CloneDashboardView.axaml:134–135`) asks `Are you sure you want to clone it again…` — "Are you sure" is filler (**V-8**); the title should carry the object (**C-1**): `Clone gitloom again?` And its scrim is `#80000000` while the clone-progress scrim above it (`:112`) is the system `#C0000000` — two scrims, one window.

**What Flow A gets right (keep, and copy elsewhere).** The `ConfirmDiscardAsync` dialog (`StagingPanelViewModel.cs:531–570`) is the app's best confirmation: it separates *revert tracked* from *remove untracked* into two named lists with exact counts and a verb-first confirm label — **C-1/C-2/C-3** by construction. The directory-delete refusal (F-A3's guard), the monotonic clone progress with cancel (**M-6**), and the auto-fetch staleness dimming (`MainWindow.axaml:242–245`, **TT-1**) are all exemplars.

### 2.2 Flow B — conflict resolution

**Walkthrough.** A merge stops on conflicts; `ConflictedFilesWindow` lists the files; the user resolves per-file (`Use Ours` / `Use Theirs`) or opens the 3-pane `ConflictResolverWindow` via `Edit…`; finally `Commit Merge` / `Continue Rebase`.

**F-B1 · "Ours" and "Theirs" — the resolver speaks git, not English, at the worst moment — S1** *(Match between system and real world).*
Every choice in the flow is labeled in raw git dialect: `Use Ours` / `Use Theirs` per row (`ConflictedFilesWindow.axaml:29–36`), `OURS (current)` / `THEIRS (incoming)` pane headers, `All Ours` / `All Theirs` bulk buttons (`ConflictResolverWindow.axaml:27–30, 55–60`). Two compounding problems: (1) the user is choosing which *work survives* using pronouns that name neither branch nor author; (2) **during a rebase, git inverts the pronouns** — "ours" is the branch being rebased *onto*, "theirs" is the user's own commits. The parenthetical `(current)`/`(incoming)` helps in a merge and actively misleads in a rebase unless the ViewModel swaps sides. This is the highest-stakes mislabel in the app: picking the wrong side silently discards the user's own changes. **Fix:** resolve the real ref names from the repo state the resolver already has and label by name with the pronoun demoted to a `TextMuted` suffix — `main — yours (ours)` / `feature/header — incoming (theirs)` — with the mapping computed per operation (merge vs rebase vs cherry-pick) in `ConflictResolverWindowViewModel`, never hardcoded. Buttons become `Keep main's version` / `Take feature/header's version` (**V-1**: name the exact object).

**F-B2 · Color says "ours is correct" — S2** *(Semantic-not-literal rule, DESIGN.md §2).*
The `OURS` header paints `SuccessBrush`, `THEIRS` paints `AccentBrush` (`ConflictResolverWindow.axaml:55–60`). Success is a *status* role; using it as a side-identity tints the user's judgment ("green = safe = pick mine") and burns the view's one accent on a pane label. **Fix:** both side headers `TextMuted` (they are labels, not states); identity is carried by the ref names from F-B1. The single accent belongs to `Mark Resolved` (`:84`), which is the view's true CTA.

**F-B3 · Bulk resolution is one click, unconfirmed, explained only on hover — S2** *(Error prevention; Bible TT-4).*
`All Ours` / `All Theirs` resolve *every remaining conflict* instantly; the consequence lives only in a tooltip (`ConflictResolverWindow.axaml:28, 30`). Recoverable until `Mark Resolved`, but the user doesn't know that — nothing states the blast radius or the way back. **Fix:** a **C**-pattern confirm naming the count (`Resolve the remaining 6 conflicts with main's version?` … `You can still review the result before marking the file resolved.`), or an inline count on the button (`All ours (6)`) plus a visible undo of the sweep.

**F-B4 · "Cancel" with unstated consequence — S2** *(User control and freedom; Bible C-2).*
The resolver footer's `Cancel` (`ConflictResolverWindow.axaml:82`) sits next to an editable Result pane the user may have hand-edited for ten minutes. Nothing states whether Cancel discards those edits, and there is no dirty-state guard visible in the view. The fear-of-loss here is rational. **Fix:** if the Result buffer is dirty, Cancel confirms with the exact fact (`Close without keeping your edits to src/App.axaml.cs? The file returns to its conflicted state.`); if not dirty, close silently. Never a confirmation when nothing is at stake (**V-7**).

**F-B5 · Multiple accents per view — S3** *(One-Accent Rule, DESIGN.md §2).* Every row of `ConflictedFilesWindow` carries an accent `Edit…` button (`:37`) — five conflicts, five accents, plus the Success-filled `Commit Merge`. The accent should sit on exactly one element: the *recommended next act* (the first unresolved file's `Edit…`), with the rest `Button.Primary`. The progress header (`HeaderText`, "N of M resolved") is good and should stay the emotional anchor of the window.

**F-B6 · The delete/modify card names no file and no side — S3** *(Visibility; V-1).* Whole-file mode says `This file was deleted on one side and changed on the other.` (`ConflictResolverWindow.axaml:42`) — generic subject, and `Keep File` / `Delete File` repeat the pronoun problem in miniature. `MissingSideNote` carries some of it; the headline should carry all of it: `release/1.2 deleted src/Legacy.cs; your branch changed it.` Buttons: `Keep the changed file` / `Delete it`.

**What Flow B gets right.** The 3-pane lock-step editor with per-chunk accept/undo is structurally excellent (per-chunk undo is exactly **V-5** as architecture); whole-file mode's verb-first `Button.Danger`/`Button.Success` pairing is correct **C-3**; the N-of-M header gives the flow a visible finish line.

### 2.3 Flow C — interactive rebase

**Walkthrough.** The user opens the plan window, reorders/rewords/squashes/drops, starts the rebase, and (often) lands in the conflict flow mid-sequence.

**F-C1 · A dropped commit doesn't look dropped — S2** *(Visibility of system status).*
The row template (`InteractiveRebaseWindow.axaml:29–49`) renders every row identically regardless of action; `Drop` — the one destructive verb on this surface — changes only a `ComboBox` value. Scanning the plan before `Start Rebase`, the user cannot see which commits are about to vanish; the fold rail (`:33`) proves the pattern exists for squash/fixup. **Fix:** a dropped row dims (`BoolToOpacityConverter` precedent) and its message gets `TextDecorations="Strikethrough"` — state carried by a non-color channel (DesignSystem E1), reversible by switching the action back.

**F-C2 · The message box edits something that may not be applied — S2** *(Match / error prevention).*
Every row carries an always-editable `NewMessage` TextBox (`:46`) whose watermark is the old message — but a reword only takes effect under the `Reword`/`Squash` actions. A user who types a better message on a `Pick` row believes they've reworded; whether the edit silently applies or silently drops, the mapping is implicit. **Fix:** typing into the box auto-promotes the row's action to `Reword` (the intent is unambiguous), with the action combo updating visibly; alternatively the box is read-only until the action makes it meaningful — the first is the kinder gesture.

**F-C3 · "Abort" before anything started — S3** *(Consistency; C-3 verbs match reality).* The plan window's dismiss button says `Abort` (`:56`) while the rebase hasn't begun — at that moment it's `Cancel`. After `Start Rebase`, *Abort* becomes the correct, git-accurate term (and is used in the staging panel's `Abort Rebase`). One word per state: `Cancel` pre-start, `Abort rebase` mid-flight.

**F-C4 · The mid-rebase conflict banner shouts, misnames the operation, and buries the count — S2** *(V-2 calm; V-1 exact object).*
When the rebase stops, `StagingPanelView.axaml:207–224` shows `Merge Conflicts Detected` — bold, `DangerBrush`, Title Case — during a *rebase*, followed by a three-sentence instruction paragraph. Severity is carried by louder words instead of the component role (**V-2**), the operation is misnamed, and the user isn't told *how many* files conflict. Microcopy §1.4 already wrote the correct copy for this exact surface. **Fix:** adopt it — the stopped-rebase pattern names the commit it stopped on and the conflicted-file count, in body type, with the way forward (`Resolve conflicts`) as the accent and `Abort rebase` as the labeled Danger path.

**F-C5 · Paper cuts — S3.** The ▲▼ reorder buttons are ~10px targets with no keyboard equivalent (`:37–42`; Alt+↑/↓ belongs in Part 3's view-local map, drag-to-reorder in I-5); the busy overlay hardcodes `#80000000` (`:62`) instead of the scrim token vocabulary — same class as F-A9's scrim drift, hand to the theme sweep; `Rebasing...` uses three dots not an ellipsis and the overlay offers no abort even though `git rebase --abort` remains valid mid-operation.

**What Flow C gets right.** The visible shortcut hint line (`:54` — `Shortcuts on the selected row: P pick · R reword · S squash · F fixup · E edit · D drop`) is the app's best piece of progressive disclosure and becomes a Part 3 pattern; the key bindings are correctly scoped so typing in the reword box is never intercepted (`:19–20` documents it); the fold rail communicates squash grouping without color.

### 2.4 The heuristic sweep — cross-cutting verdicts

| Nielsen heuristic | Verdict | Evidence (named above) |
|---|---|---|
| Visibility of system status | **Weak at the edges** | F-A2 silent stash failures, F-C1 invisible drops; strong center: N-of-M resolver header, auto-fetch staleness, monotonic clone |
| Match system ↔ real world | **Weakest area** | F-B1 ours/theirs, F-A5 "Update Project", F-A4 Rollback/Revert/Discard |
| User control & freedom | **Strong core, unfinished reach** | T-19 journal + reflog + per-chunk undo exist; F-A2/F-A3 bypass them; I-3 makes control ambient |
| Consistency & standards | **Drifting** | Title Case vs sentence case everywhere (F-A9, F-C4), two stash names, two pull names |
| Error prevention | **Split personality** | Exemplary: discard dialog, directory guard, force-with-lease-only. Absent: stash drop, file delete, bulk resolve |
| Recognition over recall | **Gap** | F-A5 junk-drawer flyout; Part 3's palette coverage is the systemic fix |
| Flexibility & efficiency | **Under-built** | 5 default shortcuts, 19 palette actions for a 40+-verb app (Part 3) |
| Aesthetic & minimalist | **Strong** | The system's core competence; findings are copy, not chrome |
| Error recognition & recovery | **Copy not yet wired** | F-A8 raw exceptions vs the finished Microcopy corpus |
| Help & documentation | **One exemplar, no system** | F-C's hint line; Part 3 §3.6 generalizes it |

**The one-sentence diagnosis:** the *engineering* of safety (journal, reflog, lease-only push, pure engines) is ahead of the *experience* of safety — the audit's S1s are all places where a shipped guarantee fails to show up at the moment of fear.

---

## Part 3 — The command surface: palette + keyboard, comprehensive and learnable

**What T-18 shipped, verified.** A genuinely good spine: `Core/Actions` is UI-free and unit-pinned (`AppAction`, `ActionRegistry` with live `CanExecute` filtering, `FuzzyMatcher` with match-span output, `ShortcutMap` with normalization/conflicts/persistence); `CommandPaletteView` renders ranked rows with category headers and **gesture chips**; `ShortcutSettingsWindow` rebinding with live conflict flagging; `MainWindow.axaml.cs` builds global `KeyBinding`s from the effective map and rebuilds on save. The palette also lists local branches and bookmarked repos. This part does not redesign that spine — it finishes it: full coverage, one naming grammar, a two-tier gesture model, and a disclosure ladder that teaches without nagging.

### 3.1 The coverage contract

**Rule P-1 (every action reachable).** *Anything invokable from a menu, flyout, toolbar, or context menu is registered in the `ActionRegistry`.* The registry — not the menus — is the app's command surface; menus become *projections* of it. This is also the phase-2 dividend: AGENTS.md already names `Actions/` as "the seam that later becomes the agent command surface," so every verb registered now is a verb an agent can be granted later.

**The gap, measured.** `MainWindowViewModel.RegisterActions()` (lines 390–455) registers **19** actions. Missing against the shipped UI:

| Reachable today from | Unregistered actions |
|---|---|
| Branch-pill flyout (`MainWindow.axaml:190–201`) | Update Project (=Pull dup), Operation History, Worktrees, Git Profiles, Accounts, SSH Keys |
| File menu (`:147–163`) | Theme ×5, Layout ×2, Keyboard Shortcuts, Exit |
| Staging panel | Stage/unstage all, discard selected, stash push/pop/apply/drop, amend toggle, composer mode toggle |
| Timeline / graph context | Checkout, create tag, cherry-pick, revert, hard-reset, interactive rebase, copy SHA, view CI checks |
| Diff viewer / staging context | File history, blame, ignore-whitespace toggle, side-by-side toggle |
| Repo actions | Push (force-with-lease), push tags, push set-upstream |

**Rule P-2 (context honesty).** An action that needs a selection registers with a `CanExecute` that says so, and the palette keeps filtering by live `CanExecute` (already shipped in `ActionRegistry.Enabled()`), so the palette never advertises a verb the moment can't honor. No "disabled rows" in the palette — an unavailable action is absent, per the shipped design.

### 3.2 The naming grammar

Palette titles are read in a 300ms glance mid-task; they need one grammar, not four. Today's titles mix `Manage Remotes…`, `View Reflog…`, bare `Pull Requests…`, and `Open Analytics` (all Title Case, against **V-8** sentence case).

**Rule P-3 (verb-first, sentence case, exact object).** `<Verb> <object>`, sentence case, Git terms keep their own casing (**N-6**). Approved verbs: `Open` (a surface), `New` (creates), `Push/Pull/Fetch/Commit/Stash/…` (the git verb *is* the verb), `Switch to`, `Copy`. **Banned:** `Manage` (says nothing — every surface manages something) and `View` as a prefix (the palette *is* viewing).
**Rule P-4 (ellipsis = more input needed).** `…` if and only if invoking opens a dialog that requires further input before anything happens. `Open reflog` has no ellipsis (it opens, done); `New branch…` keeps it (a name is required).
**Rule P-5 (search synonyms, not title bloat).** Each `AppAction` gains an optional `Aliases` list fed to the `FuzzyMatcher` haystack so `stash` finds *Shelf changes…*, `pull` finds nothing twice (F-A5's "Update Project" dies), and `undo` finds *Open operation history*. Users type their own dialect; the title stays canonical.

**Renames of the existing 19** (id stays frozen forever — Rule P-8): `Open Command Palette → Open command palette`, `Refresh Status → Refresh status`, `New Branch… → New branch…`, `Close Repository → Close repository`, `Toggle Sidebar → Toggle sidebar`, `Manage Remotes… → Open remotes`, `Manage Submodules… → Open submodules`, `Manage Git LFS… → Open Git LFS`, `View Reflog… → Open reflog`, `Pull Requests… → Open pull requests`, `Issues… → Open issues`, `Notifications… → Open notifications`, `Releases… → Open releases`, `Open Analytics → Open analytics`, `Clone / Cloud Sync → Open clone dashboard` (alias: "cloud sync"). `Commit`, `Push`, `Pull`, `Fetch` stand as-is — the git verb is the whole name.

### 3.3 The full inventory

Categories are the palette's browse-mode headers (shipped behavior); eight, in fixed display order: **Repository · Branch · Commit · Changes · Shelf · History · Hosts · Application**. New ids follow the existing dotted convention (`ActionIds.cs`).

| Id | Title | Category | Default gesture |
|---|---|---|---|
| `palette.open` | Open command palette | Application | `Ctrl+P` ✅ |
| `commit` | Commit | Commit | `Ctrl+Enter` ✅ |
| `commit.amend` | Toggle amend last commit | Commit | — |
| `commit.composer` | Toggle conventional composer | Commit | — |
| `push` | Push | Repository | `Ctrl+Shift+P` ✅ |
| `push.forceWithLease` | Force-push (with lease)… | Repository | — |
| `push.tags` | Push tags | Repository | — |
| `push.setUpstream` | Push and set upstream | Repository | — |
| `pull` | Pull | Repository | `Ctrl+Shift+L` |
| `fetch` | Fetch | Repository | `Ctrl+Shift+K` |
| `refresh` | Refresh status | Repository | `F5` ✅ |
| `repo.close` | Close repository | Application | — |
| `branch.new` | New branch… | Branch | `Ctrl+B` ✅ |
| `branch.checkout` | Switch branch… | Branch | — (palette lists branches) |
| `branch.worktree` | Open branch in a worktree… | Branch | — (I-4) |
| `branch.sweep` | Sweep merged branches… | Branch | — (I-7) |
| `tag.new` | New tag… | Commit | — |
| `changes.stageAll` | Stage all changes | Changes | — |
| `changes.unstageAll` | Unstage all changes | Changes | — |
| `changes.discardSelected` | Discard selected files… | Changes | — |
| `diff.whitespace` | Toggle ignore whitespace | Changes | — |
| `diff.layout` | Toggle side-by-side diff | Changes | — |
| `stash.push` | Shelf changes… | Shelf | `Ctrl+Shift+S` |
| `stash.pop` | Pop latest shelf | Shelf | — |
| `stash.open` | Open the Shelf | Shelf | — |
| `history.undo` | Undo last operation… | History | `Ctrl+Z`† (I-3) |
| `history.redo` | Redo operation… | History | `Ctrl+Shift+Z`† |
| `history.operations` | Open operation history | History | — |
| `reflog.view` | Open reflog | History | — |
| `history.file` | History of this file | History | — (needs selection, P-2) |
| `blame.file` | Blame this file | History | — (needs selection) |
| `rebase.interactive` | Interactive rebase… | History | — (needs commit selection) |
| `search.everything` | Search everything… | Application | `Ctrl+Shift+F` (C2, reserved) |
| `bisect.start` | Start bisect… | History | — (C1, reserved) |
| `remotes.manage` | Open remotes | Repository | — |
| `submodules.manage` | Open submodules | Repository | — |
| `lfs.manage` | Open Git LFS | Repository | — |
| `worktrees.open` | Open worktrees | Repository | — |
| `profiles.open` | Open Git profiles | Repository | — |
| `accounts.open` | Open accounts | Hosts | — |
| `sshkeys.open` | Open SSH keys | Hosts | — |
| `pullrequests.view` | Open pull requests | Hosts | — |
| `issues.view` | Open issues | Hosts | — |
| `notifications.view` | Open notifications | Hosts | — |
| `releases.view` | Open releases | Hosts | — |
| `analytics.open` | Open analytics | Application | — |
| `cloudsync.open` | Open clone dashboard | Application | — |
| `sidebar.toggle` | Toggle sidebar | Application | — |
| `theme.switch` | Theme: Midnight Watch / … (×5) | Application | — |
| `layout.switch` | Layout: Flight Deck / Conversation Deck | Application | — |
| `shortcuts.open` | Open keyboard shortcuts | Application | — |
| `keymap.show` | Show keyboard map | Application | `Ctrl+/` |

✅ = shipped default, unchanged. † = scoped, see P-6. Reserved rows exist so C1/C2 land into stable ids. Theme/layout rows follow the palette's existing parameterized-row pattern (branches are already listed as rows; themes register as five actions sharing one handler).

**Restraint rule.** Only **nine** default gestures beyond the shipped five — muscle memory is built by scarcity, not coverage. Everything else is palette-first, rebindable in `ShortcutSettingsWindow`. `Ctrl+Shift+L`/`Ctrl+Shift+K` deliberately mirror `Ctrl+Shift+P`: the *shifted* family is "talks to the remote."

### 3.4 The two-tier gesture model

**Tier 1 — global gestures** live in `ShortcutMap`, bind at the window (`MainWindow.axaml.cs`, shipped), rebindable, conflict-checked (`ShortcutMap.Conflicts()`, shipped).

**Tier 2 — view-local keys**: single-letter or arrow keys meaningful only inside one focused surface, defined on the view, **not rebindable**, and *always shown on-surface* (the F-C exemplar). The existing set: rebase `P/R/S/F/E/D` (`InteractiveRebaseWindow.axaml:22–27`). Additions:

| Surface | Keys |
|---|---|
| Interactive rebase | `Alt+↑/↓` move row (fixes F-C5) |
| Staging file lists | `Space` toggle check · `Del` discard selected (confirmed) · `Enter` open diff |
| Conflict resolver | `n/p` next/previous conflict · `1` take ours · `2` take theirs · `U` undo chunk |
| Commit graph | `y` copy SHA · `Enter` open commit |

**Rule P-6 (text traps nothing).** A gesture without modifiers never fires while focus is in a text input (the rebase window's comment at `InteractiveRebaseWindow.axaml:19–20` states the rule; it becomes law). `Ctrl+Z`/`Ctrl+Shift+Z` (I-3) additionally yield to any focused text editor's own undo — journal undo fires only when focus is outside editable text, and always confirms before acting (Part 1, I-3).

**Rule P-7 (platform respect).** Gestures are stored in the `ShortcutMap`'s neutral form; macOS renders/binds `Cmd` via the existing `meta` normalization (`CanonicalModifier`, shipped). No gesture uses a bare letter globally.

**Rule P-8 (ids are forever).** Action ids are append-only — never renamed, never reused (the Bible's ID discipline applied to code) — because they are persisted in `ShortcutBindings` and will be the agent-facing verb names.

### 3.5 The disclosure ladder — discoverable, then learnable

Five rungs, each quiet, each already voice-gated:

1. **Menus and context menus display gestures.** Every `MenuItem` whose command has a binding sets `InputGesture` from the live `ShortcutMap` (today none do — `MainWindow.axaml:147–163` renders bare headers). The menu is where users already look; the gesture rides along free.
2. **The palette shows gesture chips** (shipped, `CommandPaletteView`). The palette *is* the shortcut tutor: every use shows the faster path for next time, without saying "tip".
3. **On-surface hint lines** for Tier-2 keys — the `InteractiveRebaseWindow.axaml:54` pattern (`Label` scale, `TextMuted`, fragments per **TT** shape) replicated on the staging list and resolver. One line, always visible, never a popover.
4. **The keyboard map** (`Ctrl+/`, `keymap.show`): one scrim overlay card (radius 12, the standard overlay chrome) rendering the *effective* map — Tier-1 from `ShortcutMap` (user rebinds included), Tier-2 from a static per-view table — grouped by category, filterable by the same `FuzzyMatcher`, with a `Customize…` link to `ShortcutSettingsWindow`. Meaning survives with zero hover (**TT-4**) and zero memory.
5. **Earned hints, capped.** When a user invokes the same *gesture-bearing* action via mouse/palette three times in one session, the palette's footer line shows `Push — Ctrl+Shift+P` (bare fact, no "Tip:", no toast, no interruption — **V-3/V-7**) the next time it opens. At most one such line per session; a per-action shown-count persists in `UserPreferences` (JSON, like `ShortcutBindings`) so no hint ever repeats more than twice, ever. Delight by restraint (**M-1**'s ethos applied to teaching).

### 3.6 Learnability mechanics in the palette

- **Recency-first browse mode.** With an empty query, the shipped category-grouped list gains a `Recent` header of the last 5 invoked actions (usage log in `UserPreferences`, JSON, capped). Typed queries stay purely `FuzzyMatcher`-ranked with a mild frequency tiebreak — never let popularity beat a better string match (predictability over cleverness).
- **Match-span highlighting** (shipped) and **category chips** (shipped) stand.
- **The empty-result state** follows **ES-1/ES-3**: `No matching action` + one `TextMuted` line `Actions appear here when they can run — open a repository to see more.` — because `Enabled()` filtering makes "where did Commit go?" a real question with a real answer.

### 3.7 Acceptance gates (the part's self-test)

- **G-P1:** every command reachable in any menu/flyout/context menu has a registry id (sweep the XAML for `Command=` bindings; the list in §3.1 goes to zero).
- **G-P2:** every registered title passes the naming grammar (P-3/P-4) and the Bible's five-question gate.
- **G-P3:** `ShortcutMap.Default` plus §3.3's additions has zero `Conflicts()` and every default gesture appears in menus (rung 1) and the keyboard map (rung 4).
- **G-P4:** with a repo open, palette → type `stash` → the Shelf actions rank top-3 (alias test, P-5); with no repo open, none appear (P-2).

---

## Part 4 — Analytics dataviz: the T-22 charts, made insightful and on-brand

Designed under the `dataviz` skill's procedure (form → color-by-job → **validated** palette → mark specs → hover → accessibility → render check), with Mainguard's design system supplying the parameters. A rendered preview of everything below, across all five themes, ships as **`docs/design/assets/AnalyticsRedesign.html`**.

### 4.0 Current state, verified

`AnalyticsView.axaml` renders four LiveChartsCore charts in `SurfaceCard` cards; `AnalyticsViewModel.cs` builds the series from the pure aggregators; `Charts/ChartTheme.cs` resolves every paint from tokens. The foundations are right — the data layer is deterministic (punch card on the commit's own UTC offset, churn zero-filled and merge-excluded, contributors email-merged), and no hue is invented. What's not yet right:

1. **`ChartTheme.CategoricalPalette()` fallbacks (lines 45–49) still carry the pre-fix Midnight lanes** — including the Warning/Info collisions Part 1 of DesignSystem.md removed. Already ordered fixed (§1.6 note 1 there); restated here because the preview asset uses the corrected values.
2. **Churn lines ship 8px markers on every weekly point** (`AnalyticsViewModel.cs:219`) — the anti-pattern of a mark on every datum; at a year of history that's ~104 dots of noise.
3. **The heat ramp has 3 stops** (`ChartTheme.HeatRamp()`), which bands visibly between "one commit" and "average afternoon."
4. **Two of the four cards have no empty state**: punch card and contributors simply hide their chart (`IsVisible="{Binding HasCommitData}"`, `AnalyticsView.axaml:58, 70`), leaving bare titled cards — an **ES** gap.
5. **Headings are Title Case with units in parens** (`Language Breakdown (Bytes)`, `Top Contributors (Commits)`), and the loader says `Analyzing Repository History...` — off **V-8** (sentence case) and off **V-7** (units belong to the axis, not the headline).
6. **B-3 (backlog)**: chart legibility in Command Deck / Atelier / Aurora — resolved below by the corrected lanes plus the mandatory secondary encodings.

### 4.1 The parameters (design system → dataviz method)

| Dataviz parameter | Mainguard supplies |
|---|---|
| Categorical theme | `Lane1–Lane5` (DesignSystem.md Part 1 corrected values), fixed draw order `[Lane1, Lane2, Lane4, Lane3, Lane5]` (ChartTheme, ratified §1.6.2), sixth-plus → `TextMuted` "Other" fold — never a generated hue |
| Sequential hue | `SurfaceCard → AccentBrush` blend (one hue, light↔dark per theme polarity) |
| Polarity pair | `SuccessBrush` / `DangerBrush` — by meaning (added/removed), per the Semantic-Not-Literal rule |
| Surfaces | each theme's `SurfaceCard` (the chart card), validated per theme |
| Text | axis/labels `TextMuted`, values `TextPrimary` — **text wears text tokens, never the series color** |
| Grid | `BorderHairline`, horizontal only, recessive |

### 4.2 The validation record — computed, not eyeballed

`scripts/validate_palette.js` was run on the **corrected** lane palettes in draw order against each theme's `SurfaceCard`, and on the churn pair. Verbatim results:

| Theme | Contrast ≥3:1 | CVD adjacency (worst pair) | Lightness band / chroma floor |
|---|---|---|---|
| Midnight Watch | **pass** (all 5) | **pass** — worst ΔE 28.0 | outside band (staircase, see below) |
| Day Watch | **pass** | **pass** — worst ΔE 38.0 | `#075B55` at band edge |
| Command Deck | **pass** | **pass** — worst ΔE 15.8 | 3 above band |
| Atelier | **pass** | **FAIL** — `Lane4↔Lane2` deutan **ΔE 7.5** (< 8.0 floor) | 3 low-chroma |
| Aurora | **pass** | **pass** — worst ΔE 12.3 | 2 above band |
| Churn `Success/Danger` (Midnight) | pass | **FAIL** — deutan **ΔE 4.1** | — |

**Reading the failures honestly.** The lane tokens are locked by DesignSystem.md Part 1 and are *correct for their primary job* — 2px graph strokes ordered by a deliberate deuteranopic-lightness staircase (gate G4), which is exactly why they exceed the validator's uniform-salience lightness band: a staircase and a band are incompatible by construction, and the staircase is the ratified choice. The two hard findings that remain are therefore **mandates for secondary encoding**, which the skill defines as the legal remedy in the 8–12 floor band and below:

- **M-D1 (the donut).** Identity in the language donut must never be color-borne: every slice gets a **2px `SurfaceCard` gap** (stroke on the slice), slices ≥ 8% get a **direct label** (name + %), sub-8% slices are named only in the legend list, and the "Other" fold is always labeled. With name-on-slice, Atelier's 7.5 ΔE adjacency stops mattering — the label carries what the hue can't. (DesignSystem §1.6.2 predicted this remedy; the validator turns it from advice into a gate.)
- **M-D2 (churn).** For a deuteranopic reader the Added/Removed lines are **the same color** (ΔE 4.1). The tokens stay (they are semantically correct); the lines get non-color identity: **direct labels at the line ends** (`Added` / `Removed`, `TextMuted`) and the DesignSystem Part 2 fill-mode vocabulary — the Added series' end-marker is a **solid** dot, Removed's is a **hollow** ring (§2.5's solid-vs-hollow, reused verbatim). Grayscale-legible at any zoom.
- The punch-card ramp is sequential (one hue, monotone lightness by construction — `Blend` toward `AccentBrush`) and is CVD-safe by definition; the contributors chart is a single series (no legend needed, identity is the name axis).

### 4.3 Chart 1 — Languages (identity + share)

**Form kept:** donut, ≤ 5 + Other (shipped fold logic is right). **Elevated:**
- **Center stat** (the donut's hole earns its keep): the top language's name (`TextPrimary`, Title scale) over its share (`TextMuted`, Label scale) — `C# · 71%`. One glance answers the chart's own question.
- **M-D1 applied**: 2px `SurfaceCard` slice gaps, direct labels ≥ 8%, legend rendered as a **value list** (swatch · name · humanized size · %) right of the donut — a table-shaped legend doubles as the accessibility table view.
- **Bytes humanized** everywhere (`1.2 MB`, never `1,247,362 bytes` — tooltip keeps the exact count).
- Title: `Languages` · subtitle (`TextMuted`, Label): `by bytes of source, .gitignore-aware`. The honesty note is load-bearing (**V-6**): it names why the number differs from GitHub's.

### 4.4 Chart 2 — The punch card (magnitude on a week grid)

**Form kept:** 7×24 heat grid, full grid including zeros (shipped decision, correct — absence is data). **Elevated:**
- **Ramp: 5 stops** from exact `SurfaceCard` (zero — empty cells *are* the card, they recede completely) through the accent blend to full `AccentBrush` (peak). Monotone lightness; extend `ChartTheme.HeatRamp()` to `[1.0, 0.75, 0.5, 0.25, 0.0] toward-surface`.
- **Cell anatomy:** 1px `SurfaceCard` gap between cells (the 2px-spacer rule scaled to 24 columns), radius 2 — reads as woven fabric, on-metaphor without decoration.
- **The insight line** (this is what makes it *insightful* rather than decorative): a one-line caption under the grid, computed from the same `PunchCardStats`: `Busiest: Tuesdays 14:00–16:00 · 38% of commits land after 18:00.` Facts with counts (**V-1**), no interpretation, no "you're a night owl" cuteness (**V-3**).
- **Tooltip per cell:** `Tue 14:00 — 41 commits`. Hover target = the full cell, larger than the visual mark.
- **Timezone honesty** as a `Label`-scale caption: `Hours use each commit's own timezone.` — the deterministic own-UTC-offset bucketing is a *feature*; say it once, quietly (**V-6**).
- Title: `When work lands` · subtitle: `commits by weekday and hour`.

### 4.5 Chart 3 — Churn (polarity over time)

**Form kept:** two lines, weekly, zero-filled, merges excluded. **Elevated:**
- **2px lines, no per-point markers.** Markers appear only in the hover layer: a vertical crosshair snaps to the nearest week and shows one tooltip with both values (`Week of Jun 30 · +1,204 · −862`); the hovered week's two points get ≥ 8px markers on demand. (Kills defect §4.0.2.)
- **M-D2 applied:** line-end labels `Added` / `Removed` + solid/hollow end markers (grayscale identity).
- **The partial week is honest:** the current, incomplete week renders its final segment dashed with a `TextMuted` `partial` note — a falling line that is really just "the week isn't over" is a lie the chart must not tell (**M-6**'s honesty, applied to data).
- **Merge exclusion** stated once: caption `Merge commits excluded.` — it's why these numbers differ from `git log --stat` folklore.
- One y-axis (`lines/week`), zero-based; time x-axis with week ticks (shipped `MinStep` is right). Never a second axis.
- Title: `Churn` · subtitle: `lines added and removed per week`.

### 4.6 Chart 4 — Contributors (ranked magnitude)

**Form kept:** horizontal ranked bars, top 8, value labels at bar ends (correct for ≤ 8 ranked bars). **Elevated:**
- **The one signature accent, spent on meaning:** all bars fill with the theme's `Lane1` at rest *except the current identity's bar* (matched against the repo-local `user.name`/`user.email` the T-21 profile system already reads), which fills `AccentBrush` with a `you` chip after the name (**E4**: the chip carries text, not just color). The accent now marks *the reader in the data* — the quiet delight moment of the whole screen. When no identity matches, no bar is accented (never fake it, **V-6**).
- **Beyond-the-fold honesty:** a final `TextMuted` row `+ 14 more contributors · 212 commits` so the top-8 cut never silently misrepresents a long tail.
- **Identity-merge caption:** `Authors merged by email, case-insensitive.` — one line, prevents the "why am I listed twice/once" support question.
- Bar anatomy: 4px rounded ends **on the value end only** (baseline stays square — the skill's data-end rule), `MaxBarWidth` stays 26, 2px gaps.
- Title: `Contributors` · subtitle: `commits, all history`.

### 4.7 Shared anatomy, states, and copy

- **Tooltips** (all four charts): `SurfacePanel` background, `BorderHairline` 1px, radius 8, `TextPrimary` values / `TextMuted` labels — the overlay vocabulary at chart scale. Numbers in `FontUi` (they are quantities, not code — the One-Family rule; mono is reserved for SHAs/refs).
- **Loading:** the shipped skeleton stays; its line becomes `Reading history…` (sentence case, **V-7** — "Analyzing Repository History..." says nothing more with more words).
- **Empty states**, one per card, per **ES-1** (fixes §4.0.4): languages → `No source files to chart` / punch card & churn & contributors → the shipped `Not enough history to chart yet` + `Analytics appears once this repository has a few commits.` (verbatim from the Bible's ES example — already written, not yet wired to two of the cards).
- **Headings** move to sentence case with the subtitle pattern above; units live in subtitles and axes, never in the title parens.
- **Motion:** none. Charts appear composed (**M-2** — the graph is an instrument readout); the hover layer is state, not animation.

### 4.8 Deltas for implementers

1. `ChartTheme.cs:45–49` — fallback array → corrected Midnight lanes (`#9A9AF4, #E860A4, #C0EAE3, #DD7C10, #5066B4`); same fix in `CommitGraphCanvas.cs:32–36` (DesignSystem §1.6.1, restated).
2. `ChartTheme.HeatRamp()` — 3 stops → 5 stops (§4.4); zero stop must equal `SurfaceCard` exactly.
3. `AnalyticsViewModel.ChurnLine()` — `GeometrySize 8` → 0 at rest; hover-layer markers + crosshair tooltip; add end-labels + solid/hollow end markers (M-D2).
4. `AnalyticsViewModel.NamedPie()` — add `Stroke = SurfaceCard 2px`, direct labels ≥ 8%, center stat; humanize bytes.
5. `AnalyticsViewModel.BuildContributorSeries()` — per-bar fill (Lane1 / Accent-for-you via repo-local identity), `+N more` row, rounded value-ends.
6. `AnalyticsView.axaml` — empty-state text blocks for the punch-card and contributor cards; retitle all four cards + subtitles + captions per §§4.3–4.6.
7. Acceptance gates: **G-D1** every categorical/paired encoding passes the validator *or* carries its mandated secondary encoding (M-D1/M-D2); **G-D2** all four charts render legibly in all five themes in the render harness (closes backlog B-3); **G-D3** zero raw hex in chart code — every paint through `ChartTheme`; **G-D4** grayscale screenshot of churn + donut remains readable (the E1 gate, applied to charts).

### 4.9 The preview asset

**`docs/design/assets/AnalyticsRedesign.html`** — a self-contained, dependency-free mock of all four redesigned charts (inline SVG + CSS custom properties), with a theme switcher across the five themes. Lane values are DesignSystem.md Part 1's **corrected** palette (the spec of record; the working-tree `Themes/*.axaml` still carry pre-fix lanes — the preview shows where the fix lands). The page sets `data-palette` per theme so the dataviz validator can be re-run against it in a browser console. It exists so the M-D1/M-D2 remedies can be *seen* per theme before anyone touches the LiveCharts code. Layout eyeball-check (skill step 7) is per-theme via the switcher; the render-harness capture (G-D2) remains the shipping gate.

---

## Appendix — Self-gate record

- **Part 1** gate: every feature names its JTBD, a sketch grounded in a real Core/App seam, and a priority argument that survives "why this before that?" — checked against C1–C5 for overlap (none duplicates; I-1 explicitly delimits itself from C5's partial stash).
- **Part 2** gate: every finding pins a file (line where load-bearing), names its heuristic and severity, and ships a fix consistent with the Bible; positives ledgered so the fixes don't flatten what works.
- **Part 3** gate: G-P1–G-P4 defined and checkable; coverage gap measured against the shipped registry (19 actions) and the shipped XAML; ids frozen (P-8).
- **Part 4** gate: form/color/marks/interaction per the dataviz skill; palette **computed** (validator output quoted verbatim), failures resolved by mandated secondary encoding, not hand-waving; all five themes addressed; preview asset shipped.

