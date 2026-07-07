# P2-C3 — Multi-Repo Dashboard + Cross-Repo "My Work" — Implementation Plan

**Task ID:** P2-C3 · **Track:** client parity · **Priority:** P1 (doubles as the future swarm
control surface — P2-13 integration and P2-28 both build on it).
**Depends on:** T-10 (auto-fetch), existing repo persistence; T-23…T-27 host services for the
needs-attention lane (token-optional).
**Branch:** implement on `feature/P2-C3-multi-repo-dashboard` off `phase2`; PR targets `phase2`
(may target `main` per the client-parity footnote; default `phase2`).

> **Source of truth:** §P2-C3 of `docs/GitLoom_Master_Implementation_Document_v2.md` +
> `docs/GitLoom_Backlog.md` §A-3 (binding sketch). The needs-attention lane is the Copilot
> "My Work" / GitKraken Launchpad parity view.

---

## 0. Context — what exists today

GitLoom opens one repo at a time; repo bookmarks already persist in the DB. T-10's
`AutoFetchService` has a per-repo overlap guard. T-23…T-27 ship PRs/issues/checks/notifications
per repo. This task aggregates: a card grid of registered repos with live status + quick
actions, plus a cross-repo lane of host items needing the user.

### What you can rely on

| Fact | Where |
|---|---|
| Repo bookmarks persisted (`AppDbContext`) | existing repo persistence |
| Cheap status reads via `ExecuteWithRepo` (branch, ahead/behind, dirty, stash) | `GitServices.cs` |
| `RepositoryChanged` watcher + `AutoFetchService` cadence/overlap guard | `RepositoryWatcher.cs`, `AutoFetchService.cs` (T-10) |
| Host services: PRs (review-requested), issues (assigned), checks (failing) | T-23/T-24/T-26 services |
| Notifications mapping (T-27) | `NotificationService` |
| Dashboard-style card UI patterns | `CloneDashboardViewModel`/`RepoDashboardViewModel` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Services/IWorkspaceOverviewService.cs` + `WorkspaceOverviewService.cs` |
| **Create** | `GitLoom.Core/Models/RepoOverview.cs` (`Path, Name, Branch, Ahead, Behind, IsDirty, StashCount, LastFetched, HostSlug?`) |
| **Create** | `GitLoom.Core/Services/MyWorkAggregator.cs` (cross-repo needs-attention items) + `MyWorkItem.cs` (`Kind: ReviewRequestedPr | AssignedIssue | FailingCheck`, repo, title, url/jump) |
| **Create** | `GitLoom.App/ViewModels/WorkspaceDashboardViewModel.cs` (+ `RepoCardViewModel`) + `Views/WorkspaceDashboardView.axaml(.cs)` |
| **Edit** | main navigation (dashboard as a home surface; open-repo click-through) |
| **Edit** | repo-set persistence (add/remove from the dashboard; reuse bookmarks table) |
| **Create** | `GitLoom.Tests/WorkspaceOverviewTests.cs`, `MyWorkAggregatorTests.cs`, `RepoCardViewModelTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

```csharp
// GitLoom.Core/Services/IWorkspaceOverviewService.cs
public interface IWorkspaceOverviewService
{
    Task<IReadOnlyList<RepoOverview>> GetOverviewAsync(CancellationToken ct);   // cached; cheap reads
    event Action<RepoOverview>? RepoUpdated;   // fired on RepositoryChanged/auto-fetch refresh
}
```

- Card grid with **Fetch / Pull / Open** quick actions; persisted repo set.
- **Needs-attention lane** aggregating host items across repos: review-requested PRs, assigned
  issues, failing checks (from the shipped T-23…T-27 services).
- Later becomes the swarm's home surface (P2-13 integration) — keep the card model
  presentation-agnostic (no dashboard-only state in Core).

---

## 3. Implementation steps

1. **`WorkspaceOverviewService`:** per registered repo, one `ExecuteWithRepo` pass collecting
   branch, ahead/behind vs upstream (tracking branch; nulls when none), dirty flag (status
   scan, bounded), stash count, last-fetched (T-10 metadata). Results cached per repo with a
   staleness window; refresh triggers: `RepositoryChanged` (watcher per registered repo —
   **lazily attached**, detached for removed repos) and post-auto-fetch. Missing/moved repo path
   → `RepoOverview` with an `Unavailable` flag, never a throw that kills the batch (edge row 2).
2. **Concurrency:** reuse the T-10 cadence across the set; per-repo overlap guard already exists
   — the service must serialize per repo and parallelize across repos (bounded, e.g. 4-wide).
3. **`MyWorkAggregator`:** for each repo with a resolvable host + token: review-requested PRs
   (T-23 filtered), assigned issues (T-24 filtered), failing checks on the default branch
   (T-26). Merge → sorted lane (failing checks first). Token/host absence → repo contributes
   nothing, silently. Cached with a longer window; manual refresh button.
4. **UI:** home dashboard — repo cards (name, branch pill, ahead/behind arrows, dirty dot,
   stash badge, last-fetched age; quick actions Fetch/Pull/Open with per-card busy + typed error
   chips) + the "My Work" lane (grouped by kind, click → open repo at the right panel). Add/remove
   repos (folder picker → bookmark). Empty state invites adding repos. Design tokens; all five
   themes.
5. **Open click-through:** opens the repo in the full workspace (existing open path), dashboard
   stays navigable (back).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| ahead/behind matrix (0/0, n/0, 0/n, n/m, no upstream) | exact pills; no-upstream renders "—" |
| repo path missing/moved | card `Unavailable` with a remove affordance; batch unaffected |
| dirty + stash combinations | flags independent and correct |
| host token absent for some repos | lane shows items only from tokened repos; no errors |
| Pull with conflicts | existing typed conflict flow surfaces on the card (routes to the repo) |
| 20 registered repos | overview batch bounded-parallel; UI responsive (measured) |

---

## 5. Invariants (MUST)

1. Overview reads are cheap and cached — no full history walks; refresh is event-driven.
2. Per-repo serialization honored (no concurrent operations on one repo — the existing guard).
3. Host absence degrades silently; the local dashboard is fully offline-functional.
4. Core carries no dashboard-only presentation state.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Overview_AheadBehindDirtyMatrix` | fixtures per edge row 1 + dirty/stash combos → exact `RepoOverview` |
| 2 | `Overview_UnavailableRepo` | deleted path → flagged card, others intact |
| 3 | `Overview_EventDrivenRefresh` | `RepositoryChanged` → `RepoUpdated` fired with fresh data; cache honored otherwise |
| 4 | `Overview_BoundedParallelSerialPerRepo` | instrumented runner: ≤N concurrent, never 2 on one repo |
| 5 | `MyWork_AggregatesAndSorts` | fixture host responses → lane order (failing checks first); tokenless repos skipped |
| 6 | `Card_QuickActionErrorSurface` | fetch failure → typed chip on that card only |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** long-lived `Repository` handles across the batch (must be `ExecuteWithRepo` per
read); polling timers where events exist; host errors breaking the local grid; a second
auto-fetch scheduler.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~WorkspaceOverview|FullyQualifiedName~MyWork|FullyQualifiedName~RepoCard"
grep -rn "new Repository(" GitLoom.Core/Services/WorkspaceOverviewService.cs   # 0 hits
```

---

## 8. Definition of done

- [ ] `IWorkspaceOverviewService` (cached, event-refreshed, bounded-parallel) + persisted repo set.
- [ ] Card grid with quick actions + typed per-card errors; add/remove; open click-through.
- [ ] Cross-repo "My Work" lane from T-23/T-24/T-26 (token-optional, sorted).
- [ ] All edge rows tested; offline-complete without tokens.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-C3**.
