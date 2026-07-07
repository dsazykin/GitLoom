# P2-C2 — Global Fuzzy Search — Implementation Plan

**Task ID:** P2-C2 · **Track:** client parity · **Priority:** P1 competitive parity.
**Depends on:** T-18 (`FuzzyMatcher`), T-23/T-24 (host sources, optional — local slice works
without a token).
**Branch:** implement on `feature/P2-C2-global-fuzzy-search` off `phase2`; PR targets `phase2`
(may target `main` per the client-parity footnote — owner's call; default `phase2`).

> **Source of truth:** §P2-C2 of `docs/GitLoom_Master_Implementation_Document_v2.md` +
> `docs/GitLoom_Backlog.md` §A-2 (binding sketch). v1 conventions: typed errors, async commands,
> interface-first, tests-with-PR.

---

## 0. Context — what exists today

T-18 shipped the command palette with a pinned pure `FuzzyMatcher` (ranking already tested).
Navigation to a commit/branch/file/PR/issue is menu-diving. This task adds one search box over
everything.

### What you can rely on

| Fact | Where |
|---|---|
| Pure `FuzzyMatcher` (scored spans) + palette overlay UI | T-18, `CommandPaletteViewModel` |
| Commit walk (graph/timeline data source) | `CommitTimelineViewModel` / graph services |
| Branch/tag reads | `BranchBrowserViewModel` sources / `IGitService` |
| Working-tree/index file listing | `IGitService` status/ls surfaces |
| PR/issue services (typed, fixture-testable) | T-23 `PullRequestService`, T-24 `IssueService` |
| Jump targets: graph selection, diff/blame open, PR/issue panels | existing panel navigation |
| Shortcut registration (rebindable) | T-18 `ShortcutSettingsViewModel` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Services/ISearchAggregator.cs` + `SearchAggregator.cs` |
| **Create** | `GitLoom.Core/Models/SearchHit.cs` (`Kind, Title, Subtitle, JumpTarget, Score, MatchSpans`) |
| **Create** | `GitLoom.Core/Services/SearchSources/` — `CommitSearchSource.cs`, `BranchTagSearchSource.cs`, `FileSearchSource.cs`, `PullRequestSearchSource.cs`, `IssueSearchSource.cs` (each `ISearchSource`) |
| **Create** | `GitLoom.App/ViewModels/GlobalSearchViewModel.cs` + `Views/GlobalSearchOverlay.axaml(.cs)` |
| **Edit** | `MainWindow` shortcut wiring (`Ctrl+Shift+F`, rebindable) + palette cross-link ("search everything" mode) |
| **Create** | `GitLoom.Tests/SearchAggregatorTests.cs`, `SearchSourceTests.cs`, `GlobalSearchViewModelTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

```csharp
// GitLoom.Core/Services/ISearchAggregator.cs
public interface ISearchSource
{
    SearchHitKind Kind { get; }
    Task<IReadOnlyList<SearchHit>> SearchAsync(string repoPath, string query, int cap, CancellationToken ct);
}
public interface ISearchAggregator
{
    /// <summary>Fans the query to all registered sources, scores with FuzzyMatcher,
    /// merges + globally re-ranks, caps the result set.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string repoPath, string query, CancellationToken ct);
}
```

Sources: commits (message/sha/author), branches, tags, files, + host PRs/issues **when a token
is present**. UI: `Ctrl+Shift+F` overlay with grouped, highlighted results; Enter jumps.

---

## 3. Implementation steps

1. **Sources (local):**
   - Commits: walk the already-loaded timeline window first (instant), extend to a bounded
     `git log` walk (cap ~5k) for misses; match on subject/sha-prefix/author.
   - Branches/tags: existing list reads; match on names.
   - Files: index + working-tree listing (`ExecuteWithRepo` status/tree read); match on paths
     (FuzzyMatcher shines here).
   Each source scores via the **pure `FuzzyMatcher`** (do not re-implement scoring) and returns
   capped hits with match spans.
2. **Sources (host, optional):** PR/issue sources call the T-23/T-24 list services (cached lists
   preferred; a live search call only when the cache misses); absent token → source reports
   itself unavailable (skipped silently, edge row 3). Typed host errors never break aggregation
   (degraded, not failed).
3. **Aggregator:** fan-out with per-source timeout (e.g. 300 ms local / 1.5 s host) + global
   cancellation; merge → global re-rank (score, then kind priority tiebreak: branches/files above
   PRs for short queries) → global cap (~50). Sources that miss the deadline are dropped for
   that keystroke, not awaited.
4. **Overlay VM/view:** debounce input (~150 ms); grouped sections (Commits / Branches / Tags /
   Files / PRs / Issues) with highlighted spans (`MatchSpans` → inline runs); keyboard: up/down
   across groups, Enter jumps — commit → select in graph; file → diff/blame view; branch/tag →
   branch browser selection/checkout prompt; PR/issue → the respective panel. Esc closes;
   `Ctrl+Shift+F` rebindable via T-18 registry.
5. **Palette cross-link:** the T-18 palette gets a "Search everything…" action opening the
   overlay with the current query carried over.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| empty/1-char query | no fan-out (min length 2), overlay hint |
| keystroke while previous search in flight | previous cancelled (token), results never interleave stale-over-fresh |
| no host token | PR/issue groups absent, no error surface |
| host source timeout | local results render on time; host group dropped for that query |
| sha-prefix query (`a1b2`) | commit hit by prefix even when fuzzy score is low (exact-prefix boost) |
| huge repo file list | file source capped; measured, no UI stall |

---

## 5. Invariants (MUST)

1. Ranking goes through the pinned pure `FuzzyMatcher` — one scorer, no per-source scoring forks.
2. Aggregation is cancellation-correct: at most one in-flight search per overlay, latest wins.
3. Host absence degrades silently; local slice fully functional offline.
4. No UI-thread git/host calls.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Aggregator_MergesAndReRanks` | fixture sources → global order by score + kind tiebreak; cap honored |
| 2 | `Aggregator_CancelsPrevious` | overlapping searches → first token cancelled; no stale results delivered |
| 3 | `Aggregator_SourceTimeoutDropped` | slow fake source → others returned on deadline |
| 4 | `CommitSource_ShaPrefixBoost` | prefix query ranks the exact commit first |
| 5 | `FileSource_FuzzyPaths` | `mwvm` matches `MainWindowViewModel.cs` (spans correct) |
| 6 | `HostSources_TokenAbsent_Skipped` | no token → sources report unavailable, no throw |
| 7 | `Overlay_DebounceAndGrouping` | VM test: rapid typing → one search per debounce window; groups populated |
| 8 | `JumpTargets_Dispatch` | each hit kind → correct navigation call (spies) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second fuzzy scorer; unbounded commit/file walks; blocking fan-out; host errors
breaking local results.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~SearchAggregator|FullyQualifiedName~SearchSource|FullyQualifiedName~GlobalSearch"
grep -rn "FuzzyMatcher" GitLoom.Core/Services/SearchSources/ | wc -l   # every source uses it
```

---

## 8. Definition of done

- [ ] `ISearchAggregator` + five sources (host ones token-optional) with caps/timeouts/cancellation.
- [ ] Overlay: debounce, groups, span highlighting, keyboard jumps; rebindable shortcut + palette link.
- [ ] All edge rows tested; offline-complete local slice.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-C2**.
