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
