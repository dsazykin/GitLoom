# Mainguard Microcopy — Final Strings

**The final-strings document.** Every user-facing string in scope — hostile-git-error rewrites, destructive-safety confirmations, toasts, tooltips, and empty-state headlines — as **surface → current → final copy**, all on the voice of [`Mainguard_Voice_And_Delight_Bible.md`](Mainguard_Voice_And_Delight_Bible.md). This supersedes the Wave-2 draft inventory (this file's previous revision) and the *strings* in [`EmptyStates.md`](EmptyStates.md); that doc remains the layout/motion spec, but where a string differs, **this file wins**.

Source-of-truth hierarchy (unchanged): [`DESIGN.md`](../../DESIGN.md) and [`PRODUCT.md`](../../PRODUCT.md) govern the design system and register; [`AGENTS.md`](../../AGENTS.md) is the Repository Map that pins every row to a real view, control, or typed exception; the Voice Bible supplies the rule IDs (`V-#`/`E-#`/`C-#`/`T-#`/`TT-#`/`ES-#`) cited here. Every string below has passed the Bible's **five-question gate** (Appendix A).

**How to read the tables.** Columns: **Surface / view** (the real Repository-Map control) · **Trigger** · **Current** · **Final string** · **Rule** · **Notes** (token routing). The **Current** column is honest about provenance: `code:` quotes a string shipping today (with its throw-site or call-site), `git:` quotes the raw CLI/LibGit2Sharp text the surface would otherwise leak, `draft:` quotes the Wave-2 proposal this revision supersedes, and *(none)* means the surface currently shows nothing.

**Invariants for every row.** Refs, SHAs (7-char), and paths render in `TextBlock.Mono`; counts and relative times are invariant-culture-formatted (`22 min ago`, `12 files changed`). Destructive buttons are `Button.Danger`; cancels `Button.Secondary`; at most one `Button.Accent` per surface; toast pills radius-999 `Pill` with `OnAccent` text; severity dots by meaning (`DangerBrush`/`WarningBrush`/`InfoBrush`/`SuccessBrush`). No raw hex anywhere — token roles only, and never assume a dark theme (Daylight Loom is light).

---

## 1 · The four hostile-git rewrites (pattern `E`, flagship)

The four errors that most reliably strand a Git user, rewritten so the first sentence names the fact and the last names the way back (**E-1**, **E-3**, **E-5**). None of these ever surfaces raw library text (**E-2**).

### 1.1 `index.lock` — the founding bug

| Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|
| `GitServices.ExecuteWithRepo` stale-lock notice → `MainWindow` error overlay | A `.git/index.lock` exists when the native handle opens — the exact footgun this app exists to prevent | git: `fatal: Unable to create '…/.git/index.lock': File exists. Another git process seems to be running in this repository…` | `A .git/index.lock file is left over in mainguard/ — another Git process usually leaves this behind when it exits early. Mainguard didn't create it, so it won't remove it automatically. If no other Git process is running, delete the file and retry.` | V-1, V-6, E-1, E-3, E-5 | Neutral error panel, no severity chip. Path in `TextBlock.Mono`. `Button.Secondary` "Retry" once the user acts. Honest about the machine: Mainguard never deletes the lock itself. |

### 1.2 Detached HEAD — three surfaces, one explanation

Git's raw text (`You are in 'detached HEAD' state. You can look around, make experimental changes…` — eleven lines of advice) is the most-screenshotted scary message in Git. Mainguard splits it into a persistent state indicator, a moment-of-entry toast, and a fix-forward action (**TT-4**: the tooltip is never the sole carrier — the pill label itself names the state).

| Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|
| Navbar branch `Pill`, `RepoDashboardView` (branch selector) | `GitHeadState.IsDetached` true | *(none — pill shows a branch name only when attached)* | Pill label: `Detached at a1b2c3d` | V-1, N-6, TT-4 | SHA in `TextBlock.Mono` inside the pill. The visible label carries the state; the tooltip (§5) explains it. No `WarningBrush` — detached is a mode, not a hazard (**V-2**). |
| Checkout-detached toast, `CommitTimelineViewModel.CheckoutRevision` ("Checkout (detached)" context item, T-09) | User checks out a raw commit | git: `Note: switching to 'a1b2c3d'. You are in 'detached HEAD' state…` (11 lines) | `Checked out a1b2c3d — HEAD is detached. New commits here belong to no branch.` with `Create branch` | T-1, T-2, V-5 | `Pill`, `OnAccent`. One action, and it's the fix-forward path: `Create branch` opens the create-branch dialog pinned to this commit. |
| Tag-checkout toast, `BranchBrowserViewModel` (line 860) | User checks out a tag | code: `Checked out tag 'v1.0' (detached HEAD).` | `Checked out tag v1.0 — HEAD is detached. New commits here belong to no branch.` with `Create branch` | T-1, T-2, V-5, N-6 | Quotes dropped — the tag renders in `TextBlock.Mono` (**V-7**). Same single action as above; one explanation of "detached" everywhere (**N-6**, one term per concept). |

### 1.3 Non-fast-forward push — the rejected push

| Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|
| Push error panel, `RepoDashboardViewModel` / `BranchBrowserViewModel` push path | Remote rejects a plain push because it has commits the local branch lacks | code: `Push Failed: {ex.Message}` (toast), wrapping git: `! [rejected] main -> main (fetch first) … Updates were rejected because the remote contains work that you do not have locally.` | `origin/main has commits you don't have yet, so a plain push would leave them behind. Pull to bring them in — merge or rebase — then push. If you mean to replace the remote history, use Force-push (with lease); it confirms first.` | V-4, E-1, E-3, C-4 | **Routing fix:** this is a decision, so it moves out of the auto-dismissing toast into a panel (**T-3**). `Button.Accent` "Pull"; the force path is a plain link into the §3.1 confirmation — never a one-click force. Refs in `TextBlock.Mono`. |

### 1.4 Mid-rebase conflict — the stopped rebase

| Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|
| Conflict router → `ConflictResolverWindow` (rebase / interactive rebase) | `MergeConflictException` from `GitServices` (line 591) / `InteractiveRebaseService` (line 162) | code: `Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then click 'Continue Rebase'.` | `This rebase step conflicts in 2 files. Resolve them in the conflict resolver — saving stages each file — then Continue rebase. Abort rebase returns the branch to its pre-rebase tip; nothing is lost by stopping.` | V-2, V-5, E-1, E-3, V-8 | The `!` and "Please" go (**V-2**, **V-8**). Names both exits — forward (`Button.Accent` "Continue rebase" once clean) and back (`Button.Secondary` "Abort rebase") — and states the abort guarantee plainly. Count invariant-formatted. |

---

## 2 · Errors — remaining exception inventory (pattern `E`)

Covers the other typed exceptions in `Mainguard.Agents/Exceptions/` and the CLI-fallback paths. Shape: `[what happened + the exact object] + [what it means, in the user's terms] + [the way back]` — three sentences at most, first sentence self-sufficient (**E-5**).

| Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|
| `DeviceFlowAuthDialog` / per-host PAT dialog, routed by `AuthenticationRequiredException.Host` (T-14) | Host op attempted with no stored token | code: `A personal access token is required for {Host}.` | `Mainguard needs to sign in to github.com to push this branch. Open Accounts to connect, then try again.` | E-1, E-3 | Host in `TextBlock.Mono`. `Button.Accent` "Open Accounts"; no `DangerBrush` — actionable, not an alarm (**V-2**). |
| Push/fetch error surface, `RepoDashboardViewModel` | `AuthenticationRequiredException` with no `Host` (credentials rejected mid-transfer) | git: raw transport text | `Mainguard couldn't authenticate to the remote. Check the saved credentials for this remote in Accounts, then try again.` | E-2, E-3, E-4 | Transport text never echoed — no credential fragment can leak (**E-4**). Neutral panel. |
| Conflict router → `ConflictResolverWindow` (merge / pull) | `MergeConflictException` from merge/pull path (`GitServices` line 618) | code: `Merge conflicts detected! Please select the conflicted files in the left staging panel, resolve the conflicts in the Diff Viewer, save the files to stage them, and then commit the merge.` | `The merge stopped at 3 conflicted files. Nothing is committed yet — work through them in the conflict resolver, or Abort merge to return to where you started.` | V-2, E-1, E-3 | `Button.Accent` "Open resolver"; `Button.Secondary` "Abort merge". The reassurance ("nothing is committed yet") leads, because that's the user's first fear. |
| Conflict router → `ConflictResolverWindow` (cherry-pick) | `MergeConflictException` from cherry-pick (`GitServices` line 2118) | code: `Cherry pick resulted in conflicts. Please resolve them and commit manually.` | `Cherry-picking a1b2c3d conflicted. Resolve the files in the conflict resolver and commit to finish, or Abort cherry-pick to leave the branch unchanged.` | E-1, E-3, N-6 | `cherry-pick` hyphenated (**N-6**); SHA 7-char `TextBlock.Mono`. |
| Carry-over-conflict dialog, `BranchBrowserViewModel` (line 477) checkout path | Uncommitted changes conflict with the target branch on checkout | code: `Carry over failed due to conflicts. Would you like to stash your changes and continue checking out?` | **Title:** `Stash and switch to feature?` · **Body:** `Your uncommitted changes conflict with feature, so they can't carry over. Stash them and switch — the stash keeps every edit, and you can restore it whenever you're ready.` | C-1, C-4, E-3 | Not destructive — the stash *is* the safe path: `Button.Primary` "Stash and switch", `Button.Secondary` "Cancel". Branch in `TextBlock.Mono`. |
| Commit composer / identity-requiring ops, `StagingPanelViewModel` | `GitIdentityMissingException` | code: `No Git identity configured. Set your user.name and user.email before running Git operations.` | `Every commit is stamped with a name and email, and this repository has none set. Add one in Git Profiles, or set user.name and user.email for this repo.` | V-8, E-1, E-3 | Bible V-8 canonical example. Routes to `ProfilesWindow` (T-21); `user.name`/`user.email` in `TextBlock.Mono`. States why, never "you forgot". |
| Push / remote op, `RemotesWindow` route (`ResolveRemoteName`) | `RemoteNotFoundException` — no remote at all | code: `No remote configured for this repository.` | `This repository has no remote, so there's nowhere to push. Add one in Remotes, then push.` | E-1, E-3, V-7 | Routes to `RemotesWindow` (T-10). Neutral panel. |
| Push / remote op (ambiguous) | `RemoteNotFoundException` — multiple remotes, none tracked | code: `Multiple remotes configured and none is tracked — choose a remote explicitly.` | `This branch tracks no remote, and more than one is configured. Pick a remote in Remotes, or set an upstream so Mainguard knows the default.` | E-1, E-3 | Names both fixes. Remote names in `TextBlock.Mono`. |
| Named-remote op, `RemotesViewModel` | `RemoteNotFoundException($"No remote named '{name}'.")` | code: `No remote named 'upstream'.` | `No remote named upstream is configured for this repository. Add it in Remotes, or push to origin instead.` | E-1, E-3 | Inline in `RemotesWindow`, not a modal. Names in `TextBlock.Mono`, quotes dropped (**V-7**). |
| Operation-history panel, `OperationHistoryViewModel.ErrorMessage` | `UndoBlockedException` — dirty tree would be clobbered | code: exception reason string | `Can't undo this yet — undoing would overwrite uncommitted changes in 3 files. Commit or stash them first, then undo.` | V-5, E-3 | Bible V-5 canonical example. Inline row message, `TextMuted`; a refusal that changed nothing is not an alarm (**V-2**). |
| Operation-history panel | `UndoBlockedException` — entry non-undoable (e.g. a push) | code: `This operation cannot be undone.` | `This can't be undone from here — it published commits to a remote, and Mainguard won't silently rewind a remote. To reverse it, push a follow-up commit, or force-push (with lease) deliberately.` | V-6, E-3 | Says *why*; the row's chip already reads `Not undoable` (T-19). |
| Operation-history panel | `UndoBlockedException` — redo truncated by a newer op | code: `A newer operation replaced this one; it can no longer be redone.` | `This redo is gone — a newer operation took its place in history. What you ran instead is still here and still undoable.` | E-3 | Reassures that the current state is intact. Neutral inline text. |
| `ProfilesWindow` inline validation, `ProfilesViewModel` | `DuplicateProfileNameException` | code: `A profile named 'Work' already exists.` | `A profile named "Work" already exists. Pick a different name, or edit the existing one.` | E-1, E-3 | Inline field validation under the name box, `DangerBrush` text on the field only. Display name quoted, not mono (it's not a ref). |
| `CloneDashboardView` clone error, `CloneDashboardViewModel.CloneErrorText` | Destination exists and is non-empty | git: `destination path '…' already exists and is not an empty directory` | `The folder mainguard/ already has files in it, so Mainguard won't clone over them. Pick an empty folder or a new name.` | E-1, E-3 | Path in `TextBlock.Mono`. Inline under the form; nothing lost, no alarm. |
| Any host-API panel (PRs/Issues/Releases/Checks/Notifications) | `GitOperationException` — empty/failed host response | code: `Could not reach GitHub: …` | `Mainguard reached github.com but got no usable answer. Likely a transient host hiccup — try again in a moment. Your local repository is untouched.` | E-2, E-5, V-6 | Redacted transport text is *not* appended (**E-4**; `GitHubApiClient.Redact` already scrubs tokens). Neutral panel. |
| Worktree-create error, `BranchBrowserViewModel` (line 625) | Worktree add fails | code: `Failed to create worktree: {ex.Message}` (toast) | `That worktree wasn't created — {plain one-line reason}. The repository and its existing worktrees are untouched.` | E-2, E-3, T-3 | **Routing fix:** an error leaves the toast and lands in a panel (**T-3**). Reason de-jargoned to one clause. |
| Branch-create error, `BranchBrowserViewModel` (line 710) | Branch create fails | code: `Create Branch Failed: {ex.Message}` (toast) | `That branch wasn't created — {plain one-line reason}. Your current branch is untouched.` | E-2, E-3, T-3, V-8 | Same routing fix; mid-sentence Title Case goes (**V-8** sentence case). |
| Generic fallback, any unmapped `GitOperationException` | CLI-fallback or library op fails | code: raw library/CLI message | `That Git operation didn't complete. Mainguard made no partial change — your working tree and branches are as they were. {plain one-line reason}` | E-2, E-3, E-5, V-6 | No stack trace, no error code as headline. The no-partial-change guarantee is the sentence that matters most. |

---

## 3 · Destructive-safety confirmations (pattern `C`)

Shape: **Title** = the action as a question naming the object (**C-1**) · **Body** = what changes + what stays recoverable + the safer path (**C-2**, **C-4**) · primary `Button.Danger`, verb-first (**C-3**) · `Button.Secondary` "Cancel". Never `Button.Accent` on a destructive dialog. Match the real guarantee — journaled (T-19) or reflog-reachable (T-20) gets said (**C-5**).

| # | Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|---|
| 3.1 | Force-push confirm, `RepoDashboardViewModel.PushForceWithLeaseAsync` (T-10) | User invokes force-push (with lease) | *(none — command runs behind the push-options menu)* | **Title:** `Force-push main to origin?` · **Body:** `This replaces the remote branch with your local history. Force-with-lease refuses if anyone has pushed since your last fetch, so their work can't be silently overwritten.` | V-4, C-1, C-2, C-4 | `Button.Danger` "Force-push (with lease)" · `Button.Secondary` "Cancel". With-lease is the only force path in Mainguard — never bare `--force`. Refs in `TextBlock.Mono`. |
| 3.2 | Graph hard-reset confirm, `CommitTimelineView` context menu → `IConfirmationService` (T-09) | "Reset branch to here (hard)" | git: `git reset --hard a1b2c3d` runs unprompted in CLI | **Title:** `Hard-reset main to a1b2c3d?` · **Body:** `Commits after this point leave the branch, and the working tree is replaced to match. They stay recoverable from the reflog (Repo → Reflog) until Git garbage-collects them.` | C-1, C-2, C-5 | `Button.Danger` "Hard-reset" · "Cancel". SHA 7-char mono. Names the exact way back. |
| 3.3 | Discard-changes confirm, `StagingPanelViewModel` | Discard hunk/file edits | git: `git checkout -- <file>` (silent, irreversible) | **Title:** `Discard changes in 4 files?` · **Body:** `These edits were never committed, so discarding them can't be undone — there's nothing to recover them from. Stash instead to keep them.` | C-1, C-2, C-4 | `Button.Danger` "Discard" · `Button.Secondary` "Stash instead" · "Cancel". The one confirm with no way back — so the safer path sits inside the dialog (**C-4**). |
| 3.4 | Clean-untracked confirm, `StagingPanelViewModel` | `git clean` on untracked files | git: `git clean -fd` | **Title:** `Delete 7 untracked files?` · **Body:** `Git doesn't track these files, so deleting them removes them from disk with no way back. Review the list first — move anything you want to keep, or add it to .gitignore.` | C-1, C-2, C-3 | `Button.Danger` "Delete" · "Cancel". `.gitignore` in `TextBlock.Mono`. |
| 3.5 | Interactive rebase launch, `InteractiveRebaseWindow` | Start a rebase over N commits | git: `git rebase -i HEAD~5` | **Title:** `Rewrite the last 5 commits?` · **Body:** `Replaying gives these commits new SHAs. The originals stay reachable in the reflog until garbage collection, so a bad rebase is recoverable.` | C-1, C-2, C-5 | `Button.Danger` "Start rebase" · "Cancel". Count invariant-formatted. |
| 3.6 | Reflog restore confirm, `ReflogWindow` per-row Restore (T-20) | Restore = a confirmed hard reset | git: `git reset --hard <entry>` | **Title:** `Restore main to this entry?` · **Body:** `This hard-resets the branch to 9f8e7d6 and replaces the working tree to match. The move is itself journaled, so you can undo it from Operation history.` | C-2, C-5 | `Button.Danger` "Restore" · "Cancel". The recovery tool's own action is recoverable — say so. |
| 3.7 | Branch-delete confirm, `BranchBrowserViewModel` | Delete a local branch | code: deletes then toasts `Deleted branch 'feature'` | **Title:** `Delete branch feature?` · **Body:** `This removes the branch label. Its commits stay reachable in the reflog, and the delete is journaled — Undo brings it back from Operation history.` | C-1, C-2, C-5 | `Button.Danger` "Delete" · "Cancel". Flows into the §4 Undo toast. Matches the real guarantee — `DeleteBranch` is journaled (T-19). |
| 3.8 | Profile-delete confirm, `ProfilesWindow` (T-21) | Delete a Git profile | *(none — naive `Are you sure?` pattern)* | **Title:** `Delete profile "Work"?` · **Body:** `This removes the saved name, email, and signing settings. An Undo appears right after — a mis-click is one click back.` | C-1, C-2, C-5 | `Button.Danger` "Delete" · "Cancel". Flows into the §4 cancel-safe toast over `ProfileService.Restore`. |
| 3.9 | LFS prune confirm, `LfsWindow` → `IConfirmationService` (T-17) | Prune after a dry-run preview | git: `git lfs prune` | **Title:** `Prune 12 LFS objects (340 MB)?` · **Body:** `This deletes local LFS files that are old and already pushed — anything still on the remote can be re-pulled, and anything unpushed is left alone. The numbers above are from a dry run.` | C-1, C-2, C-4, V-6 | `Button.Danger` "Prune" · "Cancel". Honest that the preview is a dry run. Count + size invariant-formatted. |

---

## 4 · Toasts (pattern `T`)

Shape: a radius-999 `Pill`, `OnAccent` text, one line, past tense with the object (**T-1**), at most one genuinely-reversible action (**T-2**). A failure that needs a decision is never a toast (**T-3**) — see the routing fixes in §1.3 and §2. Fades per the motion budget (**M-3**); never blocks. No "Successfully" anywhere (**V-7**).

| Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|
| Merge-complete toast, `GitServices` merge / fast-forward | Merge lands | code: `Successfully merged feature into main.` (`BranchBrowserViewModel` line 378) | `Merged feature into main. 12 files changed.` | T-1, V-7 | Refs mono, count invariant. No action — nothing to reverse in-toast. |
| Merge-commit dialog result, `BranchBrowserViewModel` (line 432) | Merge committed (and optionally pushed) | code: `Merge successfully committed and pushed.` | `Merge committed and pushed to origin.` / `Merge committed.` | T-1, V-7 | Two variants replace the string concatenation. |
| Rebase-complete toast, `BranchBrowserViewModel` (line 296) / `InteractiveRebaseViewModel` | Rebase finishes cleanly | code: `Successfully rebased feature onto main.` | `Rebased feature onto main. 5 commits replayed.` | T-1, V-7 | The delight is a ~140ms fade-in (**M-1**), not a flourish. |
| Pull-with-rebase toast, `BranchBrowserViewModel` (lines 315–349) | Pull + rebase onto upstream | code: `Successfully pulled and rebased origin/feature into feature.` | `Pulled origin/feature and rebased feature onto it.` | T-1, V-7 | One sentence, both facts, no filler. |
| Pull-with-merge toast, `BranchBrowserViewModel` (lines 825–831) | Pull + merge from upstream | code: `Successfully pulled and merged origin/feature into feature.` | `Pulled and merged origin/feature into feature.` | T-1, V-7 | — |
| Checkout toast, `BranchBrowserViewModel` (line 256) | Branch checked out | code: `Checked out 'feature'` | `Checked out feature.` | T-1, V-7, V-8 | Quotes dropped (ref is mono); terminal period added. |
| Branch-created toasts, `BranchBrowserViewModel` (lines 661–705) | Branch created (± checkout, ± from ref) | code: `Created branch 'x'` / `Created and checked out 'x' from 'y'.` | `Created branch x.` / `Created and checked out x from y.` | T-1, V-7 | Refs mono, quotes dropped, punctuation consistent. |
| Branch-delete toast, `BranchBrowserViewModel` (line 542) | Branch deleted (after §3.7 confirm) | code: `Deleted branch 'feature'` | `Deleted branch feature.` with `Undo` | T-1, T-2, C-5 | The delete is journaled (T-19), so the toast carries the promise: one reversible action. |
| Rename toast, `BranchBrowserViewModel` (line 769) | Branch renamed | code: `Renamed 'old' to 'new'.` | `Renamed old to new.` | T-1 | Refs mono. |
| Push-complete toast, `RepoDashboardViewModel` / `BranchBrowserViewModel` (line 745) | Plain push succeeds | code: `Successfully pushed 'feature'.` | `Pushed 3 commits to origin/feature.` | T-1, V-7 | Bible V-7 canonical example. A force-push confirms via §3.1 first; a rejected push routes to §1.3, never a toast. |
| Tag toasts, `BranchBrowserViewModel` (lines 875–949) | Tag pushed / deleted / copied | code: `Pushed tag 'v1.0' to origin.` · `Deleted tag 'v1.0'.` · `Copied 'v1.0'.` | `Pushed tag v1.0 to origin.` · `Deleted tag v1.0.` · `Copied v1.0.` | T-1, V-7 | Quote removal only — these were already shaped right. |
| Stash-created toast, `BranchBrowserViewModel` (line 455) / `StagingPanelViewModel` | Changes stashed | code: `Changes stashed.` | `Stashed 4 files. Restore them from the Stash list.` | T-1, V-5 | Points to where the work went. |
| Clone-complete toast, `CloneDashboardView` (T-21) | Clone reaches 100% (monotonic by contract) | git: `Receiving objects: 100% … done.` | `Cloned react into ~/code/react.` | T-1, M-6 | Follows the honest progress bar resolving to 100%. Path mono. |
| Undo-result toast, `OperationHistoryViewModel` (T-19) | Undo succeeds | git: `HEAD is now at a1b2c3d` | `Undid: Commit "wire up settings". Redo puts it back.` with `Redo` | T-1, T-2, V-5 | Echoes the op description, not a SHA-only line. |
| Redo-result toast, `OperationHistoryViewModel` (T-19) | Redo succeeds | git: `HEAD is now at a1b2c3d` | `Redid: Merge feature into main.` | T-1 | No action needed. |
| Profile-delete toast, `ProfilesWindow` → `ProfileService.Restore` (T-21) | Profile deleted (after §3.8) | draft: `Profile deleted.` | `Deleted profile "Work".` with `Undo` · `Dismiss` | T-1, T-2, C-5 | The Undo *is* the product promise. |
| Timeline-filter toast, `BranchBrowserViewModel` (line 806) | Graph filtered to a branch | code: `Commit timeline filtered by feature` | `Filtered the timeline to feature.` | T-1 | Ref mono; period added. |
| No-diff notice, `BranchBrowserViewModel` (line 787) / `CommitTimelineViewModel` (line 667) | Diff requested, no differences | code: `No differences between working tree and feature.` | `No differences between the working tree and feature.` | T-1 | Kept — already on-voice; article added. |
| Dev-scaffold toast, `BranchBrowserViewModel` (line 595) | Diff backend stub | code: `Diff generation backend ready for {branch}. Connect DiffViewer UI next.` | *(remove — developer scaffolding, no user-facing string)* | V-3 | A note-to-self is not product copy; the string leaks internals to the user. |

---

## 5 · Tooltips (pattern `TT`)

Shape: a short fragment, no terminal period, revealing *why* / a boundary / the exact fix — never the visible label restated (**TT-1**). Disabled-control tooltips double as the fix (**TT-2**). Never the sole carrier of must-know state (**TT-4**). SHAs/refs in `TextBlock.Mono`.

| Surface / view | Trigger | Current | Final string | Rule | Notes |
|---|---|---|---|---|---|
| Signature badge (verified), `CommitRowViewModel` (T-15) | `SignatureStatus` Good, signer in keyring | git: `%G?` → `G` | `Verified — signed by daniel@… with a key in your keyring` | TT-3, V-6 | Badge `SuccessBrush`; the word carries the meaning, not the color alone. Signer local-part only (**E-4**). |
| Signature badge (untrusted), `CommitRowViewModel` (T-15) | Key not in keyring | git: `%G?` → `U`/`E` | `Signature can't be verified — the signing key isn't in your keyring` | TT-3, V-6 | Badge `WarningBrush`. Precisely names the trust gap — not "unsigned", not "bad". |
| Signature badge (bad), `CommitRowViewModel` (T-15) | Signature doesn't match contents | git: `%G?` → `B` | `Bad signature — the commit's contents don't match its signature` | TT-3, V-6, V-2 | Badge `DangerBrush`. Exact, not alarmist. |
| Detached-HEAD pill, `RepoDashboardView` navbar (§1.2) | `GitHeadState.IsDetached` | *(none)* | `HEAD points at a commit, not a branch — new commits here belong to no branch until you create one` | TT-1, TT-4, V-5 | Supplements the visible `Detached at a1b2c3d` label; the pill text, not the tooltip, carries the state (**TT-4**). |
| Stale-fetch label, `RepoDashboardViewModel` + `AutoFetchService` (T-10) | Fetch age > 15 min | code: bare `Fetched 22 min ago` label | `Last fetched 22 min ago — ahead/behind counts may be out of date` | TT-1 | Label dims (`TextMuted`) past 15 min; the tooltip explains the consequence, not the time. |
| Fresh-fetch label, `RepoDashboardViewModel` (T-10) | Fetch within 15 min | code: bare label | `Last fetched 2 min ago — ahead/behind counts are current` | TT-1 | Reassures the counts are trustworthy. |
| Disabled Create-PR, `PullRequestsViewModel` (T-23, line 159) | Detached/unborn HEAD | code: `HEAD is detached — check out a branch (and push it) before opening a pull request.` | `Check out a branch first — a pull request needs a branch head, and HEAD is detached` | TT-2, V-5 | The fix leads (**TT-2**). `HEAD` mono. Already close in code; reordered so the action comes first. |
| Disabled Undo, `OperationHistoryRowViewModel` (T-19) | `CanUndo` false — dirty tree | *(control greyed, no reason)* | `Undo is blocked while 3 files have uncommitted changes — commit or stash them first` | TT-2, V-5 | Mirrors the §2 `UndoBlockedException` copy, so tooltip and error agree. |
| Disabled Redo, `OperationHistoryRowViewModel` (T-19) | `CanRedo` false | *(control greyed)* | `Nothing to redo — this operation hasn't been undone` | TT-2 | States the exact condition. |
| Add-worktree disabled, `WorktreePanelViewModel` (T-21) | Branch already checked out elsewhere | *(control greyed)* | `feature is already checked out in another worktree — Git allows a branch in one worktree at a time` | TT-2, N-6 | Branch mono. One term everywhere: *worktree*. |
| Clone progress label, `CloneDashboardViewModel` (T-21) | Clone in flight | git: `Receiving objects: 63%` | `Cloning… 63% — receiving objects` | TT-1, M-6 | Monotonic, real percentage — never a jumping bar. |

---

## 6 · Empty states (pattern `ES`) — headline & body inventory

The final strings for every empty state; [`EmptyStates.md`](EmptyStates.md) keeps the layout, icon, and motion spec per row (the shared ES card, §9 there). Kinds: `empty-yet` / `not-connected` / `all-clear` / `loading` — a `not-connected` state is never an error (**ES-3**); `all-clear` earns the one quiet affirmation (**ES-4**). Headline = Hero 24/600, plain fact (**ES-1**); body = one `TextMuted` line; at most one `Button.Accent` (**ES-2**). Where a cell says *kept*, the draft string already cleared the five-question gate.

| View · kind | Current (draft) | Final headline | Final body | Action | Rule |
|---|---|---|---|---|---|
| **MainWindow** / **RepoDashboardView** · empty-yet | `No repository open` / `Open a folder that's a Git repo, or clone one from a remote.` | *kept* | *kept* | `Button.Accent` "Open repository"; `Button.Secondary` "Clone…" | ES-1, ES-2 |
| **CloneDashboardView** · empty-yet | `Clone a repository` / `Paste an HTTPS or SSH URL to clone it into a local folder.` | *kept* | *kept* | `Button.Accent` "Clone" (disabled until a valid URL) | ES-1, ES-2 |
| **StagingPanelView** · all-clear | `Working tree clean` / `No changes to stage. Every edit is committed.` | *kept* | `Every change is committed.` | None — the reward state | ES-4, V-7 — the headline already says clean; the body needn't say it twice |
| **DiffViewerView** · empty-yet | `Select a file to see its diff` / `Pick a changed file from the staging panel to view its hunks here.` | `No file selected` | `Choose a changed file in the staging panel to see its diff here.` | None | ES-1 — the headline states the absence; the body carries the instruction |
| **PreCommitFindingsView** · all-clear | `Nothing risky staged` / `No secrets, merge markers, or oversized files in this commit.` | *kept* | *kept* | None | ES-4, V-2 |
| **CommitComposerView** · empty-yet | `Compose a commit` / `Pick a type and describe the change — the message assembles as you type.` | *kept* | *kept* | None — Type dropdown is the first field | ES-1 |
| **CommitTimelineView** · empty-yet | `No commits yet` / `Make your first commit from the staging panel to start the history.` | *kept* | *kept* | None | ES-1 |
| **BlameView** · empty-yet | `Not tracked yet` / `Blame appears once this file has at least one commit.` | *kept* | *kept* | None | ES-1 |
| **BlameWindow** · empty-yet | `Nothing to blame yet` / `This file has no committed lines — its blame is empty.` | `No committed lines` | `Blame appears once this file has at least one commit.` | `Button.Secondary` "Close" | ES-1, N-6 — headline states the fact, and both blame surfaces now share one body (one term per concept) |
| **"Why this line" popover** (T-32) · empty-yet | `No pull request for this line` / `This commit wasn't merged through a pull request on the connected host.` | *kept* | *kept* | `Button.Secondary` "Open commit on host" | V-6 — states exactly what Mainguard checked |
| **"Why this line" popover** (T-32) · not-connected | `Connect a host to trace this line` / `Sign in to GitHub to see the PR and issues behind a commit.` | *kept* | *kept* | `Button.Accent` "Open Accounts" | ES-3 |
| **FileHistoryView** · empty-yet | `No earlier versions` / `This file has only its current revision — nothing to compare yet.` | *kept* | *kept* | None | ES-1 |
| **AnalyticsView** · empty-yet | `Not enough history to chart yet` / `Analytics appears once this repository has a few commits.` | *kept* | *kept* | None | ES-1 canonical |
| **AnalyticsView** · empty-yet (no languages) | `No languages detected` / `Nothing in the working tree maps to a known language — the churn and contributor charts still apply.` | *kept* | *kept* | None | ES-1 |
| **PullRequestsWindow** · not-connected | `Pull requests need a connected host` / `Sign in to GitHub to see and open PRs for this repository.` | *kept* | *kept* | `Button.Accent` "Open Accounts" | ES-3 canonical |
| **PullRequestsWindow** · empty-yet | `No open pull requests` / `Push a branch, then open a pull request from here or the branch menu.` | *kept* | *kept* | `Button.Accent` "Create pull request" (disabled per §5 tooltip on detached HEAD) | ES-1, ES-2 |
| **PR Review section** (T-25) · empty-yet | `No reviews yet` / `Add the first review — Comment, Approve, or Request changes.` | *kept* | *kept* | Verdict picker → `Button.Accent` "Submit review" | ES-1 |
| **IssuesWindow** · not-connected | `Issues need a connected host` / `Sign in to GitHub to see and open issues for this repository.` | *kept* | *kept* | `Button.Accent` "Open Accounts" | ES-3 |
| **IssuesWindow** · all-clear | `No open issues` / `Everything's triaged. Switch to Closed to see resolved issues.` | *kept* | `Switch to Closed to see resolved issues, or open a new one.` | `Button.Accent` "New issue" | V-6 — "Everything's triaged" is a claim Mainguard can't verify; zero open issues isn't triage |
| **IssuesWindow** · empty-yet | `No issues tracked` / `Open the first issue to start tracking work for this repository.` | *kept* | *kept* | `Button.Accent` "New issue" | ES-1 |
| **ChecksWindow** · not-connected | `CI checks need a connected host` / `Sign in to GitHub to see check runs for this commit.` | *kept* | *kept* | `Button.Accent` "Open Accounts" | ES-3 |
| **ChecksWindow** · empty-yet | `No checks ran for this commit` / `No CI is configured for this commit's branch, or it hasn't reported yet.` | *kept* | *kept* | None — absent CI is never a failure (`CheckStateMapper`: empty ⇒ `HasAny=false`) | ES-1, V-2 |
| **ChecksWindow** · all-clear | `All checks passed` / `Every check for this commit reported green.` | *kept* | *kept* | None | ES-4 |
| **NotificationsWindow** · not-connected | `Notifications need a connected host` / `Sign in to GitHub to see your notification inbox.` | *kept* | *kept* | `Button.Accent` "Open Accounts" | ES-3 |
| **NotificationsWindow** · all-clear | `You're all caught up` / `No unread notifications. Switch to All to see everything.` | `All caught up` | *kept* | `Button.Secondary` "All" (segment) | ES-4, V-7 — the affirmation survives two words lighter |
| **NotificationsWindow** · empty-yet | `No notifications` / `Mentions, review requests, and CI activity for this host land here.` | *kept* | *kept* | None | ES-1 |
| **ReleasesWindow** · not-connected | `Releases need a connected host` / `Sign in to GitHub to see and publish releases.` | *kept* | *kept* | `Button.Accent` "Open Accounts" | ES-3 |
| **ReleasesWindow** · empty-yet | `No releases yet` / `Publish your first release to tag a version and share notes.` | *kept* | *kept* | `Button.Accent` "New release" | ES-1, ES-2 |
| **RemotesWindow** · empty-yet | `No remotes configured` / `Add a remote to fetch, push, and open pull requests.` | *kept* | *kept* | `Button.Accent` "Add remote" | ES-1, ES-2 |
| **SubmodulesWindow** · empty-yet | `No submodules` / `This repository doesn't reference any submodules.` | *kept* | *kept* | `Button.Secondary` "Refresh" | ES-1 |
| **LfsWindow** · not-connected | `Git LFS isn't installed` / `Install Git LFS to track large files here — Mainguard detects it once it's on your PATH.` | *kept* | *kept* | `Button.Secondary` "Recheck" — Mainguard can't install it and won't pretend to | ES-3, V-6 |
| **LfsWindow** · empty-yet | `No LFS patterns tracked` / `Track a pattern like *.psd to store matching files with Git LFS.` | *kept* | *kept* | `Button.Accent` "Track pattern" | ES-1, ES-2 |
| **AccountsWindow** · empty-yet | `No accounts connected` / `Connect GitHub or another host to work with pull requests, issues, and CI.` | *kept* | *kept* | `Button.Accent` "Add account" | ES-1 — the hub every `not-connected` state points to |
| **SshKeysWindow** · empty-yet | `No SSH keys yet` / `Generate an ed25519 key to authenticate with your host over SSH.` | *kept* | *kept* | `Button.Accent` "Generate key" | ES-1, ES-2 |
| **OperationHistoryWindow** · empty-yet | `No operations recorded yet` / `Mainguard journals every commit, merge, and reset here so you can undo them.` | *kept* | *kept* | None — fills itself as you work | ES-1, V-5 — the recovery promise stated up front |
| **ReflogWindow** · empty-yet | `This ref has no reflog` / `Pick another ref, or turn on core.logAllRefUpdates to start recording its moves.` | *kept* | *kept* | The ref picker is the way forward | ES-1, V-5 |
| **ProfilesWindow** · empty-yet | `No Git identities yet` / `Create a profile to switch user.name and email per repository.` | *kept* | *kept* | `Button.Accent` "New profile" | ES-1, ES-2 |
| **WorktreeWindow** · empty-yet | `Just the main worktree` / `Add a worktree to check out another branch in its own folder — no stashing.` | *kept* | *kept* | `Button.Accent` "Add worktree" | ES-1, N-6 |
| **ConflictedFilesWindow** · all-clear | `No conflicts to resolve` / `Every file merged cleanly.` | *kept* | *kept* | `Button.Accent` "Commit merge" (merge in progress) else `Button.Secondary` "Close" | ES-4 |
| **ConflictResolverWindow** · all-clear | `All conflicts resolved` / `Both sides are reconciled — commit the merge when you're ready.` | *kept* | *kept* | `Button.Accent` "Commit merge" | ES-4 |
| **CommandPaletteView** (T-18) · empty-yet | `No actions match "<query>"` / `Try a shorter query, or press Esc to close.` | *kept* (Title scale — noted deviation; Hero would overpower the overlay) | *kept* | None — Esc dismisses, query stays focused | ES-1 |

**[Horizon] placeholders** (voice-and-naming only — not UI to build, per the Bible's scope-of-tense): *FleetView* `No agents running` / `Assign a task to an agent to start work in an isolated sandbox.` (agents named `Loom-1…Loom-n`, N-4) · *Verification view* `Nothing to verify yet` / `Verification results appear once an agent finishes a run in its sandbox.` (states stay `Verifying · Verified · Blocked · Quarantined`, N-3) · *Audit trail* `No agent activity recorded` / `Every agent action is logged here, labelled by which agent produced it and whether you've reviewed it.` (V-6).

---

## 7 · Authoring checklist

Run the Bible's **five-question gate** (Appendix A) on every new string, then check the routing:

**Do**
- Name the exact object and the way back in the same breath (**E-1**, **E-3**, **V-5**); if truly unrecoverable, say so and lead with the safer path (**C-4**).
- Front-load the fact — the first sentence must survive truncation alone; panels get three sentences at most (**E-5**, **V-8**).
- Lead confirmations with a question naming the ref (**C-1**); body = what changes + what's recoverable + the safer alternative; match the real guarantee — journaled or reflog-reachable gets promised (**C-2**, **C-5**).
- Confirm toasts in past tense with the object, one reversible action at most (**T-1**, **T-2**). "Successfully" never appears (**V-7**).
- Make disabled-control tooltips the fix (**TT-2**), keep tooltips supplementary (**TT-4**), and be exact about trust state (**TT-3**).
- Use contractions in refusals (`can't`, `won't`); sentence case; bare imperatives (**V-8**).
- Keep it invariant-culture-safe: refs/SHAs/paths in `TextBlock.Mono` (7-char SHAs); counts/times invariant.
- Pick tokens by meaning: `Button.Danger` destructive, `Button.Secondary` cancel, one `Button.Accent` per surface.

**Don't**
- No "please", "sorry", "oops", "we", mascots, emoji, or jokes in product copy (**V-3**, **V-8**).
- No exclamation marks, no "Warning!" — severity rides the component role and a plain consequence (**V-2**).
- No library/CLI internals, error codes as headlines, or stack traces (**E-2**); never echo a secret, token, or key (**E-4**).
- No "OK/Yes" on a destructive button and no `Button.Accent` on a destructive dialog — the label is the verb (**C-3**).
- No claim Mainguard can't verify — it didn't create the lock, it can't confirm "everything's triaged" (**V-6**).
- No decision-requiring failure in an auto-dismissing toast — that's a panel (**T-3**).
- No raw hex, no `StaticResource` color, and never assume the theme is dark.
