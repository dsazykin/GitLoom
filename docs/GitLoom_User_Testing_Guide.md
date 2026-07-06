# GitLoom — Manual User-Testing Guide

Hands-on tests for the features landed recently: **T-05 tags**, **T-06 partial staging**,
**T-07 worktrees**, **T-08 interactive rebase** (plus the **T-04 conflict resolver**). Everything here passed automated tests
**except** the items marked **⚠️ PRIORITY** — those are interactions or external-process/native-dialog
flows the automated suite *cannot* cover, so they're where your eyes matter most.

> **What's already machine-verified:** the UI's *rendering* and much of its *interaction* is covered by
> the **headless Avalonia render harness** (`GitLoom.Tests/Headless/*RenderHarness.cs`) — it renders real
> Views to PNGs (in `artifacts_headless/`) and even injects pointer input to drive gestures like the
> drag-select. See `GitLoom_Test_Implementation_Strategy.md` §A.6. What it **can't** judge is *feel* —
> animation smoothness, gesture responsiveness, and how the external/native flows behave on your machine.
> That's precisely what the ⚠️ PRIORITY steps below are for.

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

## 4. Interactive rebase (T-08)

Open it from the timeline: right-click a commit a few steps back → **"Interactive rebase onto here"**.
The commits **between that commit and HEAD** become the editable plan (oldest at the top).

### 4.1 Open the plan
- [ ] The **"Interactive Rebase Plan"** dialog lists those commits, each with an **action dropdown** (default **Pick**), a **short-SHA**, and an **editable message box** (pre-filled with the original message).
- [ ] The bottom shows the shortcut hint: **"Shortcuts on the selected row: P pick · R reword · S squash · F fixup · E edit · D drop"**.

### 4.2 ⚠️ PRIORITY — keyboard shortcuts on the selected row
- [ ] Click a row **on the row itself (not inside the message box)** so it highlights as selected.
- [ ] Press **D** → its action becomes **Drop**; then **P**→Pick, **R**→Reword, **S**→Squash, **F**→Fixup, **E**→Edit. Each keypress updates that row's dropdown.
- [ ] Now click **into** a row's message box and type letters (e.g. `sf`) → they should **type normally** and **not** trigger the S/F shortcuts. (Focus decides: a selected row = shortcut; the message box = plain typing.)

### 4.3 ⚠️ PRIORITY — squash/fixup fold rail
- [ ] Set any **non-first** row to **Squash** or **Fixup** → an **accent "fold rail"** appears on the **left edge** of that row, signalling it folds into the commit above. Does the grouping read clearly (including against the row-selection highlight)?
- [ ] Try setting the **first** row to Squash/Fixup → it should **refuse and snap back to Pick** (you can't squash into nothing).

### 4.4 Start disabled on an invalid state
- [ ] Make an **uncommitted edit** in the repo (dirty tree), then open the dialog → **"Start Rebase"** is **disabled**.
- [ ] Commit or stash so the tree is clean, reopen → **Start Rebase** is **enabled**.

### 4.5 Run a rebase (functional smoke — machine-tested; a quick check is plenty)
- [ ] **Reorder** two independent commits with the **▲/▼** buttons *(reorder is buttons, not drag — flag if you'd want drag)* → **Start Rebase** → history order swaps, no conflict.
- [ ] **Reword**: change a row's message box → Start → the new message shows on that commit.
- [ ] **Squash**: Pick + Squash two commits (edit the combined message) → Start → they collapse into **one** commit with your message.
- [ ] **Drop** a commit → Start → its changes are **gone** from the result.

### 4.6 ⚠️ PRIORITY — conflict mid-rebase routes to the resolver
- [ ] Craft a conflicting plan (e.g. **drop a commit a later one depends on**, or reorder two commits that touch the **same line**) → **Start**.
- [ ] The app surfaces a conflict and routes you into the **T-04 resolve flow** (select the file in the staging panel, resolve in the Diff Viewer, save to stage) → **"Continue Rebase"** → the rest of the plan completes.
- [ ] Or **Abort** at the conflict → HEAD returns **exactly** to where it was before the rebase.

---

## 5. Conflict resolver (T-04) — optional re-check

- [ ] Create a conflict: branch, edit the same lines two different ways on each side, merge → the resolver opens automatically.
- [ ] Colors: **red** where both sides edited the same existing line; **grey** where both added different new code at the same spot.
- [ ] Accept (`»`/`«`) one side → that side **and the Result** turn **green**; the Result shows that side's text live. Accept again → it **undoes**.
- [ ] Reject (`✕`) both sides → the region empties. Accept both sides of an add/add → they **stack**.
- [ ] **Mark Resolved** enables only when **every** conflict is resolved → click it; the file leaves the conflicted list, and `git log` shows a 2-parent merge commit.

---

## 6. Commit-graph interactions (T-09)

The right-click menus, ref pinning, current-branch filter, and Delete-key branch delete are **built and
machine-tested** (265 tests green, headless PNG verified). The one thing **not yet wired** is the
drag *gesture* itself — see 6.5.

### 6.1 Right-click context menu (commit)
- [ ] Right-click a commit **dot/row** in the graph → a menu opens with **Create branch here… / Create tag here… / Cherry-pick / Revert / Reset current branch here ▸ (Soft·Mixed·Hard) / Interactive rebase onto here… / Copy SHA** (plus Diff / Edit message / Go to parent·child).
- [ ] Right-click the **currently checked-out (HEAD) commit** → **"Checkout"** is **absent** (you're already there).
- [ ] With a **detached HEAD**, the **"Reset current branch here"** submenu is **hidden**.
- [ ] Right-click **empty space** (no dot) → **no menu** appears.

### 6.2 Reset + delete confirmations
- [ ] **Reset ▸ Hard** → a **confirmation dialog** appears first; cancel = nothing happens; confirm = branch moves.
- [ ] **Reset ▸ Soft / Mixed** → applies **without** a confirmation prompt (expected).

### 6.3 Ref pinning + current-branch filter
- [ ] Right-click a **branch/tag label** → **Pin** appears; click it → that ref is pinned and its lane is pulled to the **left-most** position in the graph.
- [ ] Right-click a pinned label → **Unpin** → lane ordering returns to normal.
- [ ] **Pins persist across an app restart** (close + reopen → still pinned).
- [ ] In the graph **options/filter flyout**, toggle **"Current Branch Only"** → the walk narrows to HEAD (+ its upstream). Toggle off → full graph returns.

### 6.4 Delete-key branch delete
- [ ] Click a **branch label** to select it, press **Delete** → the existing branch-delete **confirmation dialog** appears; confirm = branch gone; cancel = kept.

### 6.5 ⚠️ PRIORITY — drag-to-merge/rebase gesture (NOT YET WIRED — this is tomorrow's finish-work)
The **flyout logic and git commands are done and tested** (drag label A onto label B → **"Merge A into B"** /
**"Rebase A onto B"**, with **"Checkout B, then merge A"** when B isn't checked out; merge/rebase check the
branch out first — never an in-memory merge). What's **not** implemented is the **pointer gesture** itself
(press-drag a label, ghost following the cursor, drop-target highlight, drop threshold).
- [ ] **Nothing to test here yet** — flagged so you know the drag *feel* is the remaining piece. See the
  Overnight Report's T-09 "how to finish" note and the `// TODO(T-09 human-review)` marker in `CommitGraphCanvas`.
- [ ] *When wired:* does the drag feel smooth, does the ghost/drop-highlight read clearly, and does it behave in all five themes?

---

## 7. Remotes, auto-fetch & push options (T-10)

Remote CRUD, the resolver, push options, and auto-fetch cadence are **machine-tested** (289 green,
incl. the force-with-lease safety pair). These items need your eyes because they involve **native dialogs
and real-network** behavior the headless suite can't drive.

### 7.1 Manage remotes (native dialog)
- [ ] Open **"Manage Remotes…"** (branch flyout) → the Remotes window lists your remotes with editable **Name**/**URL** cards.
- [ ] **Edit a URL** → **Save** (enabled only once dirty) → reopen confirms it stuck. **Rename** a remote similarly.
- [ ] **Add a remote** (bottom form) → it appears in the list. **Remove** one (danger button) → it's gone.
- [ ] Open the dialog in a repo with **no remotes** → "No remotes configured yet", Add disabled until name+URL entered.

### 7.2 ⚠️ PRIORITY — Push options (real network)
- [ ] Push split-button flyout offers **Push / Force Push (with lease) / Push & Set Upstream / Push Tags**.
- [ ] **Force Push with lease** when the remote has **NOT** moved → succeeds. When the remote **HAS** moved (push from elsewhere first) → it's **refused** (lease protects you), no data clobbered. *(This is the safety-critical one.)*
- [ ] **Push & Set Upstream** on a brand-new local branch → upstream is configured (subsequent plain Push works).
- [ ] **Push Tags** → tags appear on the remote.

### 7.3 ⚠️ PRIORITY — Auto-fetch over real time
- [ ] With `AutoFetchMinutes` > 0, the **"Fetched N min ago"** label near the branch pill updates over time and **dims past ~15 min**.
- [ ] Disconnect the network → auto-fetch stays **silent** (no toast/error spam), and resumes cleanly on reconnect.
- [ ] Set `AutoFetchMinutes = 0` → auto-fetch stops.

---

## 8. Blame (T-11)

Blame computation, the bounded cache, cancellation-on-file-switch, and click-to-select are
**machine-tested** (308 green) and the gutter now **renders correctly** (verified in the headless PNG:
`author · shortSha · relative-date`, age-heat bar, commit-boundary shading). What's deferred is **visual
polish**, so your pass here is mostly aesthetic.

### 8.1 Blame renders + attributes correctly
- [ ] Open a file and toggle **Blame** on → a left **gutter** appears showing, per line, **author · short-SHA · relative date** with an **age-heat bar** (recent commits warmer, old commits dimmer) and a subtle **shade change at commit boundaries**.
- [ ] The attribution is correct (spot-check a line you know the history of).
- [ ] **Hover** a gutter row → tooltip shows the **full SHA + commit summary**.
- [ ] **Click** a gutter row → that commit is **selected in the timeline**.
- [ ] Toggle Blame **off** → gutter disappears, editor returns to normal.
- [ ] **Rapidly switch files** with blame on → the gutter always shows the *current* file's blame, never a stale one (this is machine-tested, but confirm it feels instant).

### 8.2 ⚠️ PRIORITY — gutter visual polish (the deferred bit)
The gutter is functionally complete; the remaining work is tuning the *look*. Please eyeball:
- [ ] **All five themes** (Midnight/Daylight/CommandDeck/Atelier/LoomAurora): is the age-heat ramp readable and are author/date legible in each? (heat colors come from the `BlameAgeNew`/`BlameAgeOld` tokens per theme).
- [ ] **Column width / font size** feel right (not too wide/cramped); tooltip styling.
- [ ] **Switch theme while blame is open** → the gutter should recolor live. *(If it doesn't repaint until you re-toggle, that's the known deferred item — note it.)*

---

## 9. File history (T-12)

Rename-following history, per-commit diffs (via the T-06 PatchParser), and line-history filtering are
**machine-tested** (339 green) and the view **renders correctly** (verified in PNGs: revision list + red/green
diff both visible). Mostly a smoke-check + one UX confirmation.

### 9.1 Open + browse history
- [ ] In the **staging panel**, use **"Show History"** on a file → a **File History dialog** opens (⚠️ **behavior change** — this used to *filter the commit timeline*; it now opens a dedicated dialog. Confirm you prefer this).
- [ ] Also try the **diff viewer's** right-click **"History of this file"** → same dialog.
- [ ] The dialog shows the file's revisions **newest-first**, each with message, short-SHA, author, date, and the path-at-that-commit. Selecting a revision shows its **diff vs its predecessor** (red removals / green additions).
- [ ] Select the **oldest** revision (the one that introduced the file) → the diff shows as **all additions** (`@@ -0,0 +… @@`).

### 9.2 Renames, deletes, line-history
- [ ] Open history for a file that was **renamed** in the past → history spans the rename and each row shows the **path as it was at that commit**.
- [ ] The **Line history** filter (from/to boxes + Filter) narrows the list to revisions touching that line range. *(v1 is a `git log -L` approximation — confirm it's good enough.)*
- [ ] History for a file that was **deleted** still shows its past. *(Deleted-file history intentionally does NOT follow renames in v1 — confirm acceptable.)*
- [ ] A **non-dark theme** (Daylight Loom) pass — the diff/list should stay readable (I verified Midnight via PNG).

---

## 10. Diff quality (T-13)

Intra-line emphasis, trailing-whitespace markers, ignore-whitespace mode, and image/binary detection are
**machine-tested + render-verified** (392 green; PNGs confirm "cat"→"dog" word emphasis, amber trailing-ws
boxes, and `-w` mode dropping the Stage/Discard buttons). Only the **image-diff swipe** *feel* is deferred.

### 10.1 Intra-line + whitespace (mostly aesthetic)
- [ ] Edit one word on a line → the diff **emphasizes just the changed word** (red on the old side, green on the new), not the whole line. Check **unified** and **Show Split Diff**.
- [ ] A line with **trailing whitespace** shows an **amber marker box** at the end.
- [ ] ⚠️ **All 5 themes** (esp. light **Daylight Loom**): is the word-emphasis + trailing-ws tint readable? (colors are `DiffAddedEmphasis`/`DiffRemovedEmphasis`/`DiffWhitespaceMarker` tokens.)

### 10.2 Ignore-whitespace + syntax toggles
- [ ] Make a **whitespace-only** change (reindent) → toggle **"Ignore Whitespace"** → those lines **vanish** from the diff, and the hunk's **Stage/Discard buttons + line-selection are gone** (you can't partial-stage a `-w` view — this is intended, not a bug).
- [ ] Toggle syntax highlighting (Code Editor / syntax pref) on/off → diff highlighting flips accordingly.

### 10.3 ⚠️ PRIORITY — image diff (swipe feel is PLACEHOLDER, pending your review)
- [ ] Change a **binary image** (e.g. a PNG) and open its diff → you get an **image pane** showing **before/after + a size summary** (not a garbage text diff). Non-image binaries show a size summary.
- [ ] The **swipe/onion-skin interaction is not finished** — currently before/after side-by-side + an opacity slider. The drag-to-swipe *feel* is tomorrow's build (see Overnight Report T-13 finish-list + `// TODO(T-13 human-review)` markers). Nothing to sign off here yet beyond "images render."

### 10.4 Performance (not automated)
- [ ] Open a **~5k-line diff** → scrolling/emphasis should stay smooth (~60 FPS). Flag if it janks.

---

## 11. Multi-host auth & SSH keys (T-14, offline slice)

The provider model, credential resolver, SSH key service, and the Accounts/SSH-keys pages are
**machine-tested** (427 green, incl. a **real local ssh-keygen round-trip**) and **security-audited**
(no secret in argv/URL/logs — verified by grep + tests). What's deferred is the **live auth matrix** — it
needs real accounts, so it's the ⚠️ PRIORITY work for you.

### 11.1 Pages render + local SSH keygen (safe to try now)
- [ ] Branch flyout → **Accounts…** → four host rows (GitHub/GitLab = OAuth device flow; Bitbucket/AzDO/custom = PAT paste field) + "Add another host".
- [ ] Branch flyout → **SSH Keys…** → lists your `~/.ssh` keys with type badge + fingerprint + **Copy public key**; a **Generate ed25519** panel (name/comment/passphrase).
- [ ] **Generate** a new ed25519 key (try with AND without a passphrase) → key files appear in `~/.ssh`, the list updates, Copy public key works. *(This is machine-tested but worth a real click.)*

### 11.2 ⚠️ PRIORITY — live auth matrix (needs real accounts; the deferred part)
For each, perform the auth **and** confirm the security invariant (capture a process listing during auth and grep the argv — the token/passphrase must be **absent**; also absent from logs + the remote URL):
- [ ] **GitHub** device-flow sign-in → signed-in status shown.
- [ ] **GitLab** device-flow sign-in. ⚠️ **Needs a registered GitLab OAuth application id** — `GitLabProvider.DefaultClientId` is a placeholder (`gitloom-gitlab-device-flow`). Register an app and set it first.
- [ ] **Bitbucket / Azure DevOps** — paste a PAT → validates + signs in.
- [ ] **SSH** push/pull to a host using a passphrase-protected key. NOTE: this LibGit2Sharp build has **no SSH transport**, so SSH runs through the **git CLI + system ssh/agent** (by design) — confirm that path works.
- [ ] Push/pull to a host with **no stored token** → the **Accounts page for that host** pops (not a generic error).

---

## 12. Commit & tag signing (T-15)

Signing, verification (`%G?` status), the key picker, and the config plumbing are **machine-tested** — and
unusually, the **5 gpg signing tests actually RAN here** (Git-for-Windows gpg present), not skipped. Only
the badge's **visual placement** needs your eye.

### 12.1 Sign + verify (functional — machine-tested, quick confirm)
- [ ] **View Options → SIGNING** → enable **Sign Commits & Tags**, choose a **format** (gpg/ssh) + **key** (or set a gpg program).
- [ ] Make a commit and create an **annotated tag** → confirm with `git verify-commit HEAD` and `git verify-tag <tag>` in a terminal (both should verify).
- [ ] A **bad/locked key** should fail with a **clear typed error, not a hang** (this is machine-tested via `GIT_TERMINAL_PROMPT=0`).

### 12.2 ⚠️ PRIORITY — signature badges (visual placement needs polish)
- [ ] Toggle **Show Signature Status** → commit rows show a **shield badge**: **green** = verified, **amber** = signed/untrusted (unknown key), **red** = bad signature; unsigned rows show none.
- [ ] ⚠️ **Badge placement**: in the headless render the badge slightly **crowds the commit message text** — this is the deferred `// TODO(T-15 human-review): signed-badge visual` item (glyph/size/placement). Please check spacing/alignment in the real app across the 5 themes and note what you'd want adjusted.

---

## 13. Submodules (T-16)

The status mapper (all 2^14 flag combos), CLI ops, and the panel are **machine-tested** (470 green) and the
panel **renders correctly** (PNG confirms all four status chips + path-with-spaces). Human items are the
real-network ops + native dialog feel.

### 13.1 Panel + status (quick confirm)
- [ ] Open **Submodules…** (sidebar) in a repo that has submodules → a panel lists each submodule with **path, short-SHA, URL, and a status chip**: green **Up to date**, amber **Modified**, red **Dirty**, muted **Not initialized**.
- [ ] Uninitialized rows correctly **hide "Open as repo"** (nothing to open yet).
- [ ] A repo with **no submodules** shows a clean empty state.

### 13.2 ⚠️ PRIORITY — real ops (network / dialog feel)
- [ ] **Update all (init)** against a repo with a **real remote submodule** (https/ssh) → the submodule clones/inits (automated tests use local file:// fixtures, so a genuine network init is unverified).
- [ ] **Update to remote** on a submodule → advances it to the remote tip.
- [ ] **Sync URLs** after editing a submodule URL in `.gitmodules`.
- [ ] **Open as repo** on an initialized submodule → it opens as a top-level GitLoom repo (window swap is wired but wasn't driven headlessly).
- [ ] Visual pass across the 5 themes (chips inspected via Midnight Loom only).

---

## 14. Git LFS (T-17)

The LFS service, pure parsers, and panel are **machine-tested against real git-lfs 3.5.1** (509 green, **39 LFS
tests actually ran**, 0 skipped) and the panel **renders correctly** (PNG confirms toggle + patterns + objects
with Downloaded/Pointer chips + path-with-spaces). Only real-remote object transfer is a human item.

### 14.1 Panel + local ops (mostly machine-verified — quick confirm)
- [ ] Open **Git LFS…** (repo menu) → panel with an **Enable Git LFS** toggle, a **Tracked patterns** section (track input + Untrack per pattern), and an **LFS objects** list with **Downloaded** (green) / **Pointer** (grey) chips.
- [ ] **Track** a pattern (e.g. `*.psd`) → it appears; add a matching binary + commit → it's stored as an LFS pointer. **Untrack** removes the pattern.
- [ ] In a repo **without git-lfs installed**, actions fail with a clear **"Git LFS is not installed."** message (not a crash).
- [ ] A **pointer file diff** shows **"LFS object (size)"** instead of raw pointer text.

### 14.2 ⚠️ PRIORITY — real LFS remote (network — the deferred item)
- [ ] Against a **real LFS-enabled remote**: **Pull objects** downloads the actual binaries through the authenticated path — and confirm (process listing) that **no token appears in any argv/log** during the transfer.
- [ ] **Prune** → the dry-run preview lists what would be removed; confirm → it prunes.

---

## 15. Command palette & keyboard shortcuts (T-18)

The fuzzy matcher (pinned scoring), action registry, shortcut map, and palette are **machine-tested**
(544 green) and the palette **renders with highlighted match spans** (PNG-verified, incl. a key-input-driven
render). Human items are keyboard *feel* + rebinding.

### 15.1 Palette (mostly machine-verified — quick confirm)
- [ ] **Ctrl+P** opens the palette; the query box **auto-focuses**. Type to fuzzy-filter across **actions + local branches + bookmarked repos**; matched characters are **highlighted**; rows show a **category chip** and any **gesture chip** (e.g. Ctrl+B).
- [ ] Empty query → grouped browse (Branch/General/Repository/View).
- [ ] Selecting a row runs the **real** action (not a stub).

### 15.2 ⚠️ PRIORITY — keyboard feel + rebinding
- [ ] **Focus/Escape**: Ctrl+P focus lands reliably; Escape closes; clicking the scrim closes, clicking the card doesn't.
- [ ] **Arrow Up/Down** move selection **skipping category headers** and wrap; **Enter** runs the selected row; single-click runs a row.
- [ ] Default gestures: **Ctrl+Enter** commit, **Ctrl+Shift+P** push, **F5** refresh, **Ctrl+B** new branch — and with **no repo open**, repo-actions no-op while Ctrl+P still works.
- [ ] **File → Keyboard Shortcuts…**: rebind one (e.g. Refresh → Ctrl+R); induce a **conflict** (two actions, same gesture → red flag + banner); **Save** → the new gesture fires immediately and **survives an app restart**; Reset-to-defaults works.

---

## 16. Operation journal — undo/redo (T-19)

Every mutating op is journaled and round-trips (Undo restores refs+HEAD byte-exactly; Redo re-applies) —
**machine-tested over every op kind** (569 green; 21 mutating methods wrapped). The history UI renders
correctly (PNG-verified). Mostly a confidence smoke-test + a few semantic confirmations.

### 16.1 Undo/redo (machine-verified — quick confirm)
- [ ] Open **Operation History…** (repo menu) → a list of your operations, newest first, each with a **kind + description + timestamp** and an **Undo** (or **Redo** if already undone) button; non-undoable ops (push/pull/stash-pop) show **"Not undoable"** with a disabled Undo.
- [ ] Make a commit → **Undo** it → the commit is gone and the change is back as **unstaged edits** (mixed reset to parent, like `git reset HEAD~`); **Redo** → re-commits.
- [ ] Create/delete a branch or tag → Undo → it's restored/removed; branch-delete undo also restores **upstream config**.

### 16.2 ⚠️ semantics worth a human sanity-check
- [ ] **Undo with a dirty working tree** (uncommitted edits) → refused with a typed message, **nothing changes** (commit/stash/discard first).
- [ ] **StashPush undo** removes the stash ref but does **not** restore working-dir changes (journal is ref-based) — real recovery is via the stash list. Confirm this reads acceptably.
- [ ] **Pull (rebase strategy)** records **two** entries (non-undoable Pull + undoable Rebase) — confirm that reads OK in the list.
- [ ] Interactive-rebase + live-push journaling (tested only under RequiresGitCli) — give a manual pass.

---

## 17. Reflog viewer & recovery (T-20)

The reflog read + the two recovery actions (both journaled via T-19) are **machine-tested** (587 green) and
the panel **renders correctly** (PNG-verified). Quick confirm + a recovery sanity-check.

### 17.1 Browse + recover (mostly machine-verified)
- [ ] Open **Reflog…** (repo menu, or command palette "View Reflog…") → newest-first list of where the ref has pointed: **from→to SHA, message, timestamp**. Switch the **Ref** picker between HEAD and local branches.
- [ ] **Restore** on an entry → confirmation prompt (warns about losing uncommitted tracked changes) → hard-resets the current branch to that entry. Then open **Operation History** and confirm you can **Undo** it (it's journaled).
- [ ] **Create branch here** on an entry → type a name → a branch is created at that commit (recovers a lost/orphaned tip).

### 17.2 ⚠️ worth a human eye
- [ ] Recover a **deleted branch**: delete a branch, open HEAD reflog, find its tip, **Create branch here** → confirm the branch is back at the right commit.
- [ ] Confirm-dialog wording + the inline create-branch editor feel; rendering across all 5 themes (I verified Midnight Loom via PNG).

---

## 18. Git profiles, worktree panel & clone progress (T-21)

Profiles (apply/CRUD/cancel-delete), the worktree panel, and the clone backend (progress + cancellation)
are **machine-tested** (620 green) and both UIs **render** (PNG-verified). Only the progress-bar *animation
feel* defers.

### 18.1 Git profiles (machine-verified — quick confirm)
- [ ] Open **Git Profiles…** (repo menu) → create/edit identities (name, user.name/email, signing). **Apply** writes the open repo's **local** `user.name`/`user.email` (+ signing config) — NOT global.
- [ ] **Delete** a profile → an **Undo** toast lets you cancel the delete (cancel-safe).
- [ ] Duplicate profile name is rejected.

### 18.2 Worktrees panel
- [ ] Open **Worktrees…** → list worktrees; **Add worktree** from an existing or new branch; the add is **disabled when the chosen branch is already checked out** elsewhere.
- [ ] ⚠️ Remove/prune touch the filesystem — verify on a real repo.

### 18.3 ⚠️ PRIORITY — clone progress (backend done; animation feel is the deferred bit)
- [ ] Clone a repo → a live overlay shows **"Receiving objects N/M"**, a **percentage bar**, and a **Cancel clone** button.
- [ ] **Cancel** mid-clone → the partial folder is **deleted** (machine-tested).
- [ ] Cloning into a **non-empty dir** is refused with a clear error.
- [ ] ⚠️ The one deferred item: the progress-bar **animation smoothness** (does it ease between reported percents or jump?) — `// TODO(T-21 human-review)` in `CloneDashboardView.axaml`. Tune the fill transition and eyeball across themes.

---

## 19. Analytics (T-22)

The analyzer (gitignore-aware language breakdown, weekly churn, punch-card, contributors) is **machine-tested
with pinned numbers** (633 green) and all four charts **render legibly in dark AND light** (PNG-verified).
Mostly a chart-readability glance across the remaining themes.

### 19.1 Charts (machine-verified — quick confirm)
- [ ] Open **Analytics** on a real multi-contributor repo → four charts: **Language Breakdown** donut, **Code Churn per Week** (green Added / red Removed lines), **Commit Punch Card** (weekday×hour heatmap), **Top Contributors** bars.
- [ ] Numbers look right (churn excludes merges + binaries; contributors merge same-email identities; punch-card buckets by the commit's own timezone).
- [ ] Switching away mid-analysis **cancels cleanly** (no stall); the skeleton/loader shows then clears on a big repo.

### 19.2 ⚠️ PRIORITY — chart readability across themes
- [ ] Eyeball each chart in **all five themes** (I rendered Midnight + Daylight only). The categorical **lane palette** has an inherent lightness-band overlap (fixed design-system hues) mitigated by legend + labels — do a **"can I tell the donut slices apart?"** check, especially **Command Deck / Atelier / Loom Aurora**.
- [ ] Punch-card heat ramp visible against each theme's surface; churn green/red distinguishable; bar labels readable.

*(Optional future enhancement, not built — not in the T-22 contract: per-HEAD SQLite result caching + live-streaming partial results. Say the word if you want them.)*

---

## 20. Pull request integration (T-23, offline slice)

The host-agnostic PR service, GitHub provider (JSON parsing), `IsSupported` logic, and VM gating are
**machine-tested against checked-in JSON fixtures** (675 green) and **token-secure** (verified: token only in
the `Authorization` header — never a URL/log/exception). The panel **renders** (PNG-verified). The **live
round-trip against a real GitHub account is the deferred item.**

### 20.1 Panel + graceful states (offline-verified)
- [ ] Open **Pull Requests…** (repo menu, branch right-click "Create pull request", or command palette) → panel with a **Create pull request** button and (when connected) a list of open PRs: number, title, author, source→target, DRAFT badge, per-row **Merge** (method picker) / **Close** / **Open in browser**.
- [ ] With **no GitHub token**, or an **unsupported host**, the panel shows a **"Not connected"** affordance (sign in via Accounts) — it never errors or blocks the app.
- [ ] Create-PR is **disabled on a detached/unborn HEAD** with a hint to push a branch first.

### 20.2 ⚠️ PRIORITY — live PR round-trip (host-account-gated; the deferred matrix)
Needs a real GitHub account with a token stored under `token_github.com` (connect via **Accounts**, §11):
- [ ] **List** open PRs on a real repo.
- [ ] **Create a draft PR** from a pushed branch → confirm it appears on github.com.
- [ ] **Merge** it with **squash** → confirm merged on the host. **Close** a different PR.
- [ ] An **invalid/expired token** routes to re-auth without leaking the token (check no token in any log).
- [ ] (Later) repeat once GitLab/Bitbucket/Azure adapters land — they currently throw a typed "not yet supported for &lt;host&gt;".

---

## What to report back

For each ⚠️ PRIORITY item, a simple **"feels right"** / **"here's what's off (step N: …)"** is enough.
Everything else is already covered by automated tests, so a quick smoke-check is plenty.
