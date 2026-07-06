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
