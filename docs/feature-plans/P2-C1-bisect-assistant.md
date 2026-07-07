# P2-C1 — Interactive Bisect Assistant — Implementation Plan

**Task ID:** P2-C1 · **Track:** client parity (any time; good phase2 warm-up) · **Priority:** P1
competitive parity (GitKraken/Fork power features).
**Depends on:** T-19 (journal), T-32 (culprit → PR context, optional). No host account, no
platform dependencies.
**Branch:** implement on `feature/P2-C1-bisect-assistant` off `phase2`; PR targets `phase2`
(client-parity tasks *may* target `main` — decide with the repo owner; default is `phase2`).

> **Source of truth:** §P2-C1 of `docs/GitLoom_Master_Implementation_Document_v2.md` +
> `docs/GitLoom_Backlog.md` §A-1 (the binding sketch). v1 conventions apply: typed errors, async
> commands, interface-first, tests-with-PR, journal integration where HEAD moves.

---

## 0. Context — what exists today

No bisect anywhere (`grep -rn "bisect" GitLoom.Core` → expect 0 hits). `git bisect` is
CLI-driven state under `.git/BISECT_*`; the policy split (G-7) puts it on the **CLI runner**
(`RunGitChecked`), with repo reads via `ExecuteWithRepo`.

### What you can rely on

| Fact | Where |
|---|---|
| Hardened CLI runner `RunGitChecked(repoPath, args...)` (typed nonzero-exit) | `GitLoom.Core/Services/GitServices.cs` |
| `ExecuteWithRepo` for reads; typed exceptions | same |
| `IOperationJournal` (T-19) — journal HEAD moves; dirty-tree refusal pattern | `GitLoom.Core/Services/OperationJournal.cs` |
| Commit context card (blame → PR jump, T-32) | `CommitContextService` / `BlameCommitContextViewModel` |
| Wizard/dialog UI patterns | `GitLoom.App/Views/*Dialog*`, `ViewModels` |
| `TempRepoFixture` for offline repo tests | `GitLoom.Tests` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Services/IBisectService.cs` + `BisectService.cs` |
| **Create** | `GitLoom.Core/Models/BisectState.cs` |
| **Create** | `GitLoom.Core/Services/BisectLogParser.cs` (pure) |
| **Create** | `GitLoom.App/ViewModels/BisectWizardViewModel.cs` + `GitLoom.App/Views/BisectWizardView.axaml(.cs)` |
| **Edit** | entry points: commit context menu ("Bisect from here…") + command palette action |
| **Create** | `GitLoom.Tests/BisectServiceTests.cs`, `BisectLogParserTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

```csharp
// GitLoom.Core/Models/BisectState.cs
public sealed class BisectState
{
    public bool InProgress { get; init; }
    public string? CurrentSha { get; init; }        // candidate checked out for testing
    public int RemainingCommits { get; init; }
    public int EstimatedStepsLeft { get; init; }    // ceil(log2(remaining))
    public bool Done { get; init; }
    public string? CulpritSha { get; init; }        // set when Done
}

// IBisectService
BisectState StartBisect(string repoPath, string badSha, string goodSha);
BisectState MarkGood(string repoPath);
BisectState MarkBad(string repoPath);
BisectState MarkSkip(string repoPath);
void ResetBisect(string repoPath);                  // abort → restore original HEAD
```

All transitions via `RunGitChecked` (`bisect start/good/bad/skip/reset`); state read via a pure
`BisectLogParser` over `BISECT_LOG` + `git bisect` porcelain output.

---

## 3. Implementation steps

1. **`BisectLogParser` (pure):** parse `BISECT_LOG` lines (`git bisect good <sha>` etc.) and the
   runner's stdout from mark commands (`Bisecting: N revisions left to test after this (roughly
   K steps)`; culprit block `<sha> is the first bad commit`). Output: remaining, steps, current,
   done+culprit. Fixture corpus from captured real runs (incl. skip-heavy runs and the
   "merge base must be tested" preamble).
2. **`BisectService`:**
   - `StartBisect`: **dirty-tree refusal first** (typed, same rule as undo); validate both shas
     exist (`ExecuteWithRepo` lookups); journal the operation (T-19 — record original HEAD so
     abort/undo restores it); run `bisect start <bad> <good>`; parse → state.
   - Marks: refuse (typed) when no bisect in progress; run the verb; parse; when done, run
     `bisect reset` is **not** automatic — the wizard shows the culprit and offers
     "Finish (reset)" (the culprit card needs the session's log).
   - `ResetBisect`: `bisect reset` → journal completes; HEAD restored (assert).
   - Every mark checks out a new candidate — HEAD moves stay inside the bisect session; the
     single T-19 journal entry brackets the whole session (start→reset), not one per step.
3. **Wizard UI:** launched from a commit's context menu ("Bisect: this commit is bad…") or the
   palette. Step 1: pick bad (default HEAD) + good (tag/commit picker). Step 2 (repeating): big
   **Good / Bad / Skip** buttons + progress ("~N steps left", remaining count) + current-candidate
   card (sha, subject, author) + **Abort**. Step 3: **culprit card** — commit details + T-32
   context (PR link when resolvable) + actions (open in graph, blame, copy sha) + Finish.
   All git work off the UI thread (`Task.Run` + `IsBusy`); typed-exception routing to inline
   errors.
4. **Detached-HEAD note:** the graph/timeline already renders detached HEAD (T-05 work);
   verify the badge shows during a session.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| dirty working tree at start | typed refusal, nothing started |
| good == bad / good not ancestor semantics | git's error surfaced typed; no half-started session |
| `MarkSkip` narrowing to "only skipped commits left" | git's inconclusive result surfaced; state `Done=false` with a typed outcome message |
| abort mid-session | HEAD restored to original; `BISECT_*` files gone |
| mark with no session in progress | typed refusal |
| culprit found | `Done=true`, `CulpritSha` set; reset only on Finish |

---

## 5. Invariants (MUST)

1. Bisect verbs via the CLI runner only; parser is pure and fixture-tested (v1 G-7 split).
2. The session is journaled (T-19); abort restores the original HEAD.
3. Dirty-tree refusal before any mutation.
4. Offline-verifiable end-to-end — no network anywhere.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Parser_FixtureCorpus` | captured logs/porcelain → remaining/steps/current/done/culprit |
| 2 | `Bisect_FindsKnownCulprit` | fixture repo, bug introduced at commit k of n → scripted good/bad marks → `CulpritSha == k` |
| 3 | `Bisect_SkipPath` | skip at each step still lands (or reports inconclusive per git) |
| 4 | `Start_DirtyTree_Refused` | typed, repo untouched |
| 5 | `Reset_RestoresHead` | abort mid-run → original HEAD, no `BISECT_LOG` |
| 6 | `Marks_WithoutSession_Typed` | refusal matrix |
| 7 | `Journal_BracketsSession` | one journal entry; undo after finish restores pre-bisect HEAD |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** LibGit2Sharp-driven bisect (no such API — any attempt means hand-rolled state);
per-step journal spam; auto-reset that destroys the culprit context; UI-thread git calls.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Bisect"
grep -rn "bisect" GitLoom.Core/Services/BisectService.cs | grep -v RunGit   # verbs go through the runner
```

---

## 8. Definition of done

- [ ] `IBisectService` + pure parser per contract; journaled, dirty-refusing, offline.
- [ ] Wizard (pick → good/bad/skip loop with progress → culprit card with T-32 context → finish/abort).
- [ ] All edge rows tested incl. the known-culprit fixture end-to-end.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-C1**.
