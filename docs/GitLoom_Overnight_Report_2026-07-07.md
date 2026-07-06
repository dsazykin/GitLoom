# GitLoom — Overnight Implementation Report (2026-07-07 morning)

This is the status report from the unattended overnight run. It records, per task, what
was **built + verified**, what was **deferred to you** (with a precise finish-list), and any
**issues**. Hands-on UI checks live in `GitLoom_User_Testing_Guide.md` (⚠️ PRIORITY items).

**Plan:** implement the fully machine-verifiable backend/offline slice of every remaining
task (T-09 … T-23), one at a time via fresh subagents, each independently re-verified and
squash-merged to `main`. Only genuine human-feel / live-external-account bits deferred.

_Last updated: (run in progress)._

---

## Summary table

| Task | Slice built | State | Deferred to you |
|---|---|---|---|
| **T-09** | full backend (hit-tester, menus, pinning+migration, current-branch filter, drag flyout logic, Delete-key) | ✅ merged | drag *gesture feel* only (flyout logic done) |
| **T-10** | full (remote CRUD, resolver, push options, auto-fetch) | ✅ merged | native dialog + real-network push/fetch checks |
| **T-11** | full backend + working gutter (service, LRU cache, VM, cancellation) | ✅ merged | gutter *visual polish* across the 5 themes |
| **T-12** | full (rename-following history, per-commit diff, line-history filter, dialog UI) | ✅ merged | confirm "Show History" UX change; non-dark theme glance |
| **T-13** | full engine (intra-line, whitespace, ignore-ws, image/binary detection) | ✅ merged | image-diff **swipe** control feel only |
| **T-14** | offline slice (providers+registry, SSH key service, credential resolver, Accounts/SSH pages) | ✅ merged | **live auth matrix** (real GitHub/GitLab/PAT/SSH) |
| **T-15** | full (sign commits/tags, %G? verification, key picker, badges) — gpg tests RAN | ✅ merged | signature badge **placement** polish |
| **T-16** | full (pure status mapper, list + CLI ops, panel) | ✅ merged | real-network submodule init/update; dialog feel |
| **T-17** | full (LFS service, pure parsers, panel) — 39 LFS tests RAN | ✅ merged | real LFS remote push/pull of objects |

---

## Per-task detail

### T-09 — Rich commit-graph interactions ✅ merged (PR TBD)
**Built + verified (265 tests green, build/format clean, migration applies, PNG inspected):**
- Pure `GraphHitTester` (row/node/label targeting) + right-click context menus built in `CommitTimelineViewModel` with context rules (hide Checkout on HEAD commit; hide Reset when detached/unborn; no menu on empty space).
- `IGitService.GetHeadState`/`CreateBranchAt`; hard-reset + branch-delete gated behind `IConfirmationService`; all mutating menu actions on the async/`IsBusy`/typed-exception path.
- **Ref pinning persisted**: `PinnedRef` entity + `AddPinnedRefs` EF migration + `PinnedRefService`; `CommitGraphRouter` reserves left-most lanes for pinned tips (invisible until realized; **zero change when nothing is pinned** — regression-guarded + asserted).
- **"Current branch only"** walk filter (HEAD + upstream) with a view toggle.
- **Drag flyout logic** (label A → label B): Merge/Rebase actions with checkout-gated wording; git ops check the branch out first (never in-memory merge).
- **Delete key** on a selected ref label → branch delete through the confirmation dialog.

**Deferred to you (feel only):** the drag **pointer gesture** in `CommitGraphCanvas` — press-drag threshold, ghost label following the cursor, drop-target highlight. The flyout content, commands, and git behavior are all done and tested; only the gesture needs wiring + feel tuning. See `// TODO(T-09 human-review)` in `CommitGraphCanvas` and User-Testing Guide §6.5 for the step-by-step finish plan.

**Note:** drag-merge/rebase **conflicts** currently surface as a notification (typed-exception path) rather than auto-opening the conflict resolver — consistent with the async invariant. If you'd prefer the resolver on drag-merge conflicts, that's a small follow-up.

### T-10 — Remotes, auto-fetch & push options ✅ merged
**Built + verified (289 tests green, build/format clean, PNGs inspected, security-audited):**
- Remote CRUD (`GetRemotes`/`AddRemote`/`RemoveRemote`/`RenameRemote`/`SetRemoteUrl`) with pre-mutation name validation + typed throws.
- **`ResolveRemoteName` resolver** replacing every hardcoded `"origin"` (tracked-branch upstream → `origin` → sole remote → `RemoteNotFoundException`), exposed as `GetDefaultRemoteName`; remote-named `Fetch` overload.
- **Push options**: `PushForceWithLease` (**`--force-with-lease` only — I confirmed no bare `--force` on any push path**), `PushTags`, `PushSetUpstream`.
- **`AutoFetchService`**: single `PeriodicTimer` loop off the UI thread on the `AutoFetchMinutes` cadence (0 = off), per-repo overlap guard, skip-while-operating, counted-not-toasted failures; deterministic test seams (interval/clock/`RunCycleAsync`). Owned + disposed by `RepoDashboardViewModel`.
- UI: `RemotesWindow` manager, push split-button flyout, "Fetched N min ago" label (dims >15 min).

**Deferred to you (native/real-network only — no code left unwritten):** open/close/save round-trips in the native `RemotesWindow`; real-network **force-with-lease** (moved vs unmoved — the safety-critical check), Push Tags, Push & Set-Upstream; and auto-fetch behavior over real elapsed time + on a disconnected network. See User-Testing Guide §7.

**I independently verified:** the sole remaining `--force` in the codebase is the pre-existing `RemoveWorktree` (T-07), not a push; the `origin` resolver fallback is the only `"origin"` literal left.

### T-11 — Blame ✅ merged
**Built + verified (308 tests green, build/format clean, gutter PNG inspected):**
- `IGitService.GetBlame` (per-line `BlameLine`, 1-based line numbers, typed `GitOperationException` on a path missing at revision) via `ExecuteWithRepo`; `InvalidateBlameCache`.
- `BlameCache`: bounded (~32-entry) LRU keyed `(repoPath, path, headSha)`, invalidated per-repo on `RepositoryChanged` (never unbounded).
- `BlameViewModel`: loads off the UI thread on `Task.Run` with a `CancellationToken` **cancelled on file switch** (rapid switching never renders a stale gutter — pinned by a test); click-a-line → commit selection via messenger.
- `BlameView` + `BlameGutterMargin`: age-heat bar + `author · shortSha · relative-date` + commit-boundary shading + tooltip; age-heat colors from per-theme tokens.

**⚠️ I caught and fixed a real bug during verification:** the gutter was rendering at **0 width** (invisible) because `SetLines` called only `InvalidateVisual()` — the margin never re-measured when blame arrived after the initial empty sync. Added `InvalidateMeasure()`; re-ran the harness and confirmed the gutter now paints correctly (see the PNG). The subagent was cut off (my session limit) before it could catch this; I finished the Repository Map, format fix, docs, and this fix myself.

**Deferred to you (visual polish only — the gutter is functional):** age-heat ramp readability + author/date legibility across all 5 themes, column width/font metrics, tooltip styling, and live recolor on a theme switch while blame is open. See User-Testing Guide §8.2 and the `// TODO(T-11 human-review)` marker.

### T-13 — Diff quality ✅ merged
**Built + verified (392 tests green, build/format clean, 3 PNGs inspected):**
- Pure Core engines: `IntraLineDiff` (DiffPlex word-level changed spans, surrogate-safe), `WhitespaceMarkers` (trailing-ws runs), `ImageDiffDetection` (image-candidate + binary sniff + size summary). All UI-free → unit-tested with **pinned exact ranges**.
- `GetFileDiff(..., ignoreWhitespace)` (`git diff -w`, `--cached` when staged) + `GetBlobBytesAtCommit`.
- UI: `IntraLineDiffTextBlock` renders precomputed spans as styled Runs (theme-token brushes, recolor on theme change); wired into unified + side-by-side. Ignore-whitespace toggle **hides partial-staging** (buttons + selection off) — render-proven. Persisted `SyntaxHighlightDiffs` pref. `DiffAdded/RemovedEmphasis` + `DiffWhitespaceMarker` tokens in **all 5 themes**.
- **PNGs confirm** (I inspected all 3): word-level "cat"→"dog" emphasis (red/green), amber trailing-ws boxes, and `-w` mode dropping the Stage/Discard buttons + reindent.

**Deferred to you (image-diff swipe *feel* only — detection + before/after render are done + reachable):** the onion-skin/drag-to-swipe interaction on `ImageDiffControl`. Finish-list: (1) map pointer-X → `SwipePosition` in `ImageDiffControl.axaml.cs`; (2) overlay before+after in one panel (clip or opacity-crossfade the top image by `SwipePosition`) instead of side-by-side; (3) choose onion-skin vs. wipe; (4) eyeball decoded bitmaps in the real app across 5 themes (headless can't decode real PNGs, so bitmap decode is currently untested). Markers in `ImageDiffControl.axaml(.cs)` + `ImageDiffViewModel.SwipePosition`. See User-Testing Guide §10.3.

**Not machine-verifiable (manual):** "~5k-line diff holds 60 FPS" is a profiling item (§10.4).

### T-14 — Multi-host auth + SSH key manager (offline slice) ✅ merged — **unblocks T-17**
**Built + verified (427 tests green, build/format clean, PNGs inspected, security-audited by me):**
- `IHostProvider` + `GitHubProvider`/`GitLabProvider` (device flow) + `Bitbucket/AzureDevOps/GenericProvider` (PAT); `HostProviderRegistry.Resolve`; the GitHub device flow refactored into a reusable `DeviceFlowClient` (`GitHubAuthClient` preserved as a facade — Clone Dashboard behavior intact).
- `SshKeyService` (ed25519 keygen via `ProcessStartInfo.ArgumentList` — never a shell string; `~/.ssh` listing; copy pub; passphrase in keyring). `CredentialResolver` (single source for SSH-vs-token). `SecureKeyring` gains a storage-dir override + null-on-corrupt. `AuthenticationRequiredException` carries the host → unknown-host-no-token routes to the per-host PAT dialog.
- Accounts + SSH Keys preferences pages (VMs + Views).

**Security — I independently re-ran the audit (this task's whole point):** ✅ ssh-keygen uses `ArgumentList` only (no shell string); the one `-N <passphrase>` argv is the plan-sanctioned *local* keygen exception, safe from shell-splitting and kept off every network path + out of stderr; ✅ no secret concatenated into any URL (OAuth token goes in POST body/headers, URLs are bare hosts); ✅ no secret logged; ✅ `TokenUsername` single-sourced through `GitHostDetector.UsernameForToken` (no duplicate host→username switch). A **real local ssh-keygen round-trip** test generates a key + round-trips the passphrase through the keyring and asserts the passphrase is absent from both key files.

**Deferred to you (live matrix — needs real accounts):** live GitHub + GitLab device-flow login, live Bitbucket/AzDO PAT validation, live SSH auth + host-side public-key registration, and the live process-listing argv no-secret check. See User-Testing Guide §11.2. **Two caveats:** (1) GitLab needs a **registered OAuth app id** — `GitLabProvider.DefaultClientId` is a placeholder; (2) this LibGit2Sharp build has **no SSH transport**, so SSH push/pull runs through the git CLI + system ssh/agent (the codebase's existing path) and `CredentialResolver` returns a Core `SshUserKeyCredentials` value object, not the libgit2 type.

### T-15 — Commit & tag signing ✅ merged
**Built + verified (447 tests green; the 5 gpg signing tests ACTUALLY RAN here, not skipped; format clean; PNG inspected):**
- Signing driven by a `SignCommits` preference: `Commit`/`CreateTag` switch to the git CLI (`git commit` / `git tag -s`) after writing `commit.gpgsign`/`tag.gpgsign`/`gpg.format`/`user.signingkey`/`gpg.program` to **local** repo config; unsigned path stays on LibGit2Sharp. `GIT_TERMINAL_PROMPT=0` → a bad/locked key fails typed, never hangs (proven by an async timeout-race test).
- Pure `SignatureStatusParser` (`%G?` → status); `GetSignatureStatuses` batch-reads only when the timeline signature column is on; `ListSigningKeys` (gpg secret keys / `~/.ssh/*.pub`) backs the key picker; green/amber/red shield badges on commit rows.
- **The subagent solved a real gpg gotcha** for the test fixture: Git-for-Windows' MSYS gpg-agent rejects a `GNUPGHOME` socket path containing the drive-letter colon, so it sets `GNUPGHOME` in MSYS `/c/…` form; ephemeral throwaway home + passphrase-less ed25519 key; real keyring never touched.

**Security (re-audited by me):** no `--passphrase` on any production path (only the test's empty-passphrase keygen); signing config written Local-only.

**Deferred to you (visual only):** signature **badge placement** — in the headless PNG the badge crowds the message text slightly (glyph/size/placement polish). Signing + verification + config + key picker are all done and tested. See User-Testing Guide §12.2.

### T-16 — Submodules ✅ merged
**Built + verified (470 tests green, build/format clean, PNGs inspected, security-checked):**
- Pure `SubmoduleStatusMapper` (LibGit2Sharp flags → Uninitialized/Modified/Dirty/UpToDate, precedence pinned; tested over **all 2^14 flag combos**). `GetSubmodules` (reads via `ExecuteWithRepo`, status via mapper, path-sorted) + CLI-driven `UpdateSubmodules`(`--init --recursive`) / `UpdateSubmoduleRemote` / `SyncSubmodules` via `RunGitChecked`.
- `SubmodulesWindow` + VM: per-row status chip, Update-to-remote, Open-as-repo (routes through `MainWindowViewModel.OpenRepository`), Update-all/Sync/Refresh; async off-thread + typed errors.
- **Security-checked (me):** `protocol.file.allow` appears **only in tests**, never production Core — confirmed by grep. Integration tests use local file:// fixtures (fresh-clone init, inner-commit modified, dirty tree, multiple entries, path-with-spaces, missing `.gitmodules`, sync).

**Deferred to you (network / dialog feel — no code unwritten):** real-remote (https/ssh) submodule init/update, Open-as-repo window swap end-to-end, and the native dialog visual pass across themes. See User-Testing Guide §13.2.

### T-17 — Git LFS ✅ merged (was unblocked by T-14)
**Built + verified (509 tests green; 39 LFS tests incl. 10 RequiresGitLfs against real git-lfs 3.5.1 ACTUALLY RAN, 0 skipped; build/format clean; PNG inspected):**
- `ILfsService`/`LfsService`, CLI-driven end-to-end (never libgit2): cached `git lfs version` probe → typed "Git LFS is not installed." degrade; install/uninstall(--local), track/untrack, list patterns, ls-files, pull, prune(dry-run summary), per-repo enable state. **`lfs pull` reuses the T-14 authenticated CLI path — no token in argv/URL.**
- Pure unit-tested parsers: `LfsPointer` (pointer detection + size/oid), `LfsLsFilesParser`, `LfsAttributesParser`; `LfsFile` model.
- `LfsWindow` + VM: enable toggle, tracked patterns, LFS objects with Downloaded/Pointer chips, Pull, Prune (dry-run preview → confirm). Diff viewer renders "LFS object (size)" instead of raw pointer text.
- **I verified:** LFS tests run (not skip) here; format clean; PNG shows all rows incl. a path-with-spaces object.

**Deferred to you (network only):** real LFS remote **Pull objects** (download actual binaries through the authenticated path + confirm no token in argv/log) and a live Prune. See User-Testing Guide §14.2.
