# GitLoom — Manual User-Testing Guide

Hands-on tests for the features landed this session: **T-05 tags**, **T-06 partial staging**,
**T-07 worktrees** (plus the **T-04 conflict resolver**). Everything here passed automated tests
**except** the items marked **⚠️ PRIORITY** — those are interactions or external-process/native-dialog
flows the automated suite *cannot* cover, so they're where your eyes matter most.

Tick each box as you go. If something misbehaves, note the step number and what you saw.

---

## 0. Setup

1. **Close any running GitLoom instance** (the app locks its own `.exe`; a build won't succeed while it's open).
2. Launch:
   ```bash
   "/mnt/c/Program Files/dotnet/dotnet.exe" run --project GitLoom.App
   ```
3. Open a repository with some history. Easiest scratch repo (run in a terminal, then open its folder in GitLoom):
   ```bash
   mkdir /tmp/gl-test && cd /tmp/gl-test && git init
   git config user.name you && git config user.email you@example.com
   printf 'alpha\nbravo\ncharlie\ndelta\necho\nfoxtrot\n' > sample.txt
   git add . && git commit -m "seed"
   printf 'one\ntwo\nthree\nfour\n' > second.txt
   git add . && git commit -m "second file"
   ```
4. **Theme sweep (do this once at the end of each section):** File → Theme → switch between
   **Midnight Loom** and **Daylight Loom**. Nothing should become unreadable, and no element should
   stay dark-on-dark or light-on-light. The design must not assume "dark."

---

## 1. Tags (T-05)

### 1.1 Create a tag from a commit
- [ ] Right-click a commit in the timeline → **"Create tag here…"**.
- [ ] Dialog shows the **target commit short-SHA** (accent color), a **Tag Name** box, an **Annotated** checkbox, and **Cancel**/**Create**.
- [ ] Type an invalid name (e.g. `bad name` with a space, or `-x`) → **Create** should be usable but the operation is rejected with an error (no tag created). Names like `v1.0.0` succeed.
- [ ] Tick **Annotated** → a message box appears. Enter a message, **Create**.
- [ ] The new tag appears as a **chip** on that commit in the detail panel (right side) — a **neutral pill with a tag glyph**, visually distinct from the violet branch chips.

### 1.2 Tags section in the branch browser (sidebar)
- [ ] Expand the **Tags** category in the left sidebar → your tags are listed.
- [ ] Click a tag → its flyout menu offers **Checkout**, **Push to origin**, **Delete from origin**, **Copy name**, **Delete**.
- [ ] **Copy name** → paste elsewhere confirms the tag name is on the clipboard.
- [ ] **Checkout** a tag → the app moves to a **detached HEAD** at that commit (branch label/badge reflects detached state).
- [ ] **Delete** → confirmation dialog → after confirming, the tag disappears from the list and its chip is gone.
- [ ] (If you have a remote) **Push to origin** then **Delete from origin** — no crash; a notification confirms each.

---

## 2. Partial staging (T-06)

Make some edits first: in the app or a terminal, change **two separated regions** of `sample.txt`
(e.g. edit line 2 and line 6) and save. Select `sample.txt` (the **unstaged** entry) in the staging panel.

### 2.1 Unified view — hunks
- [ ] The diff shows as **hunk cards**: each has a `@@ … @@` header and **Stage** (green) + **Discard** (red) buttons.
- [ ] Click **Stage** on one hunk → that hunk moves to staged; the panel updates; the other hunk remains unstaged.
- [ ] Select the file's **staged** entry → the same hunk now shows an **Unstage** button (blue). Click it → it returns to unstaged.
- [ ] Click **Discard** on a hunk → a **confirmation dialog** appears first; after confirming, that hunk's changes revert in the file (the other hunk is untouched).

### 2.2 ⚠️ PRIORITY — Unified view, drag-select lines
- [ ] Click a single changed (`+`/`-`) line → it highlights (**violet left rail + tint**). Click again → deselects. Context lines don't select.
- [ ] **Press and drag** down across several changed lines → they should **paint-select** as the pointer passes. Release to stop.
- [ ] Start a drag on an **already-selected** line and drag → it should **paint them off** (deselect the swept lines).
- [ ] With lines selected, a bottom bar appears: **Stage selected lines** / **Discard selected lines** / **Clear**.
- [ ] **Stage selected lines** → only those exact lines move to staged; the rest of the hunk stays unstaged. **Clear** empties the selection.
- [ ] *(This is the interaction automated tests can't judge for feel — does the drag feel smooth and predictable?)*

### 2.3 ⚠️ PRIORITY — Side-by-side view, block accept/discard (resolver-style)
- [ ] Click **"Show Split Diff"** (top-right).
- [ ] Each modified block shows **old (left) | new (right)** columns with a small action row: **✓ (stage)**, and **✕ (discard)** — plus **↺ (unstage)** when viewing the staged side.
- [ ] Click **✓** on a block → that whole block is staged (moves out of the unstaged diff).
- [ ] Click **✕** on a block → confirmation → the block's change is discarded from the working tree.
- [ ] On the **staged** entry, the block shows **↺** → clicking it unstages the block.

### 2.4 Staleness
- [ ] Select a file, then edit that same file **externally** (terminal) so it changes on disk, then click a **Stage**/**Discard** in the app. You should get a banner: **"The file changed on disk — selection reset, try again."** (no silent misbehavior).

---

## 3. Worktrees (T-07)

### 3.1 ⚠️ PRIORITY — "Diff working tree against this commit"
*(Automated tests cover the diff generation, but not this menu action opening the patch externally.)*
- [ ] Make an uncommitted edit somewhere in the repo.
- [ ] Right-click a commit → **"Diff working tree against this commit"**.
- [ ] The whole-tree diff opens in your OS default `.patch` viewer, showing your working-tree changes vs that commit.
- [ ] Right-click a commit when the working tree is clean → you get a notification like **"No differences…"** instead of an empty file.

### 3.2 ⚠️ PRIORITY — Add a worktree (native folder picker)
*(The folder picker is a native dialog automated tests can't drive.)*
- [ ] Find the **"New worktree"** action for a branch (branch context menu in the sidebar).
- [ ] Pick an **empty target folder** outside the repo → a notification confirms the worktree was created, and the folder now contains a checkout of that branch.
- [ ] Try adding a worktree for the **currently checked-out** branch → you should get a clear error notification (git refuses), not a crash.

---

## 4. Conflict resolver (T-04) — optional re-check

- [ ] Create a conflict: branch, edit the same lines two different ways on each side, merge → the resolver opens automatically.
- [ ] Colors: **red** where both sides edited the same existing line; **grey** where both added different new code at the same spot.
- [ ] Accept (`»`/`«`) one side → that side **and the Result** turn **green**; the Result shows that side's text live. Accept again → it **undoes**.
- [ ] Reject (`✕`) both sides → the region empties. Accept both sides of an add/add → they **stack**.
- [ ] **Mark Resolved** enables only when **every** conflict is resolved → click it; the file leaves the conflicted list, and `git log` shows a 2-parent merge commit.

---

## What to report back

For each ⚠️ PRIORITY item, a simple **"feels right"** / **"here's what's off (step N: …)"** is enough.
Everything else is already covered by automated tests, so a quick smoke-check is plenty.
