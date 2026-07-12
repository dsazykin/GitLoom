# P2-34 — Context Vault: Persistent Cross-Repo Knowledge Index — Implementation Plan

**Task ID:** P2-34 · **Milestone:** M7.75 · **Priority:** P0 — MergeLoom's second-strongest
feature, countered.
**Depends on:** P2-06 (bare mirrors); feeds P2-09 spawns, P2-11 review, P2-27 intake.
**Branch:** implement on `feature/P2-34-context-vault` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated index/pack fixtures + **retrieval-quality model eval advisory**.
> Delta sync, budgets, SHA pinning, and degraded sources are deterministic. Whether packs actually help agents is a model-in-the-loop eval (sample tasks, human-graded relevance) — run before marketing the Context Vault claim, not in the PR gate.
>
> **Source of truth:** §P2-34 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** evidence is **Git-native and navigable** — every item pins the commit SHA
> it was read at and links to blame (T-11)/file history (T-12); the cockpit shows which evidence
> influenced which hunk. **v1 is symbol/path/FTS retrieval — deliberately not a vector DB.**

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-34 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-34** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-34 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

Workers spawn with only the task prompt; MergeLoom attaches "Context Engine" evidence to its PRs.
This task builds the daemon-side index over the ext4 bare repos (symbols, docs, rule files),
kept current by Git-object-keyed delta sync, and the budget-capped `ContextPack` attached to
every worker spawn and rendered in review.

### What you can rely on

| Fact | Where |
|---|---|
| Bare mirrors + provisioner fetch hook; keep-alive cadence | P2-06/P2-09 |
| Bare-repo object reads without locks (radar established the pattern) | P2-19 |
| Tree-sitter native packaging (grammars pinned) | P2-19 `SymbolOverlap` |
| Daemon SQLite (FTS5 available) | P2-02 |
| Worker spawn prompt-context injection point | P2-09 |
| Blame/file-history for evidence links | T-11/T-12 services |
| Audited-transport discipline for external sources | `GitHubApiClient` pattern |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Context/ContextVault.cs` (`IContextVault` + impl) |
| **Create** | `GitLoom.Core/Context/Indexers/SymbolIndexer.cs` (Roslyn for C#; tree-sitter top languages; plain-text fallback), `DocIndexer.cs` (markdown), `RuleIndexer.cs` (`AGENTS.md`/`CLAUDE.md`) |
| **Create** | `GitLoom.Core/Context/VaultStore.cs` (SQLite FTS5 schema: items, repo/sha keys, kinds) |
| **Create** | `GitLoom.Core/Context/PackBuilder.cs` (scoped retrieval, budget cap, confidence scoring) |
| **Create** | `GitLoom.Core/Context/External/ConfluenceSource.cs`, `NotionSource.cs` (read-only, keyring-auth, one audited transport each) |
| **Edit** | P2-09 spawn path (attach pack), P2-11 cockpit (render pack + evidence links), P2-27 draft (pack into drafting prompt) |
| **Create** | `GitLoom.App/ViewModels/Context/ContextPackViewModel.cs` (+ evidence list with blame/history links) |
| **Create** | `GitLoom.Tests/VaultIndexTests.cs`, `DeltaSyncTests.cs`, `PackBudgetTests.cs`, `EvidencePinningTests.cs`, `ExternalSourceDegradedTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// daemon GitLoom.Core/Context/ContextVault.cs
public sealed record EvidenceItem(string Kind /* symbol|api|doc|rule|history */, string SourcePath,
    string Excerpt, string CommitSha, double Confidence);
public sealed record ContextPack(string TaskId, IReadOnlyList<EvidenceItem> Items, string RulesDigest);
public interface IContextVault
{
    void Index(string repoHash);                     // baseline walk: symbols, public APIs, docs, AGENTS.md rules
    void DeltaSync(string repoHash, string fromSha, string toSha);  // Git-object-keyed incremental update
    ContextPack BuildPack(string taskId, string query, IReadOnlyList<string> repoHashes, ContextBudget budget);
}
```

---

## 3. Implementation steps

1. **Baseline `Index`:** tree walk of the **bare repo** at its current main (object reads via
   `git cat-file`/LibGit2Sharp-on-bare through the daemon runner — never working trees, never
   locks). Classifier routes files: C# → Roslyn symbol extraction (types, public members, doc
   comments); top languages → tree-sitter symbol nodes; markdown → heading-chunked doc items;
   `AGENTS.md`/`CLAUDE.md` → rule items (the concatenated digest of enabled rule files =
   `RulesDigest`); binary/huge files skipped by classifier + size caps (edge row 2). Every item
   stores `CommitSha` = the sha it was read at.
2. **`DeltaSync`:** `git diff --name-status fromSha..toSha` → re-index touched paths (rename =
   delete+add; delete removes items). Wired to run after each provisioner fetch and keep-alive
   cycle. Cheap, exact, **no file watching**.
3. **Store:** SQLite FTS5 table (excerpt, path, kind) + metadata columns; per-repo namespace by
   `repoHash`. Embeddings deliberately out — a rejection-worthy scope creep for v1.
4. **`PackBuilder`:** query (task title/prompt/scope terms) → FTS + symbol-name matches across
   the requested repos, filtered by per-repo include/exclude path rules; ranked (match quality ×
   kind weight; rules always included first); truncated to `ContextBudget` (max items + max total
   chars — enforced, edge-tested). Confidence = normalized rank score. Packs are **immutable per
   task** (stored on the task; rebuild = new pack for a new task).
5. **Consumers:** P2-09 spawn prepends the pack (rules first, then evidence excerpts with
   path@sha headers); P2-27 drafting prompt includes it; P2-11 renders the pack panel — each
   evidence item links to blame/file-history **at its recorded SHA**, and hunk-influence is
   surfaced by matching evidence paths to diff paths (v1: path-level influence chips).
6. **External sources (optional):** Confluence/Notion read-only adapters — one audited transport
   each, tokens `ctx_<source>` keyring header-only (G-4); items carry source URLs instead of
   shas. Unreachable → pack builds without them, flagged `Degraded` (edge row 4).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| repo re-provisioned from scratch | vault detects the new root, full re-index |
| binary/huge files | skipped by classifier + size caps |
| rules file changed mid-task | pack pins the digest it shipped with; next task gets the new rules |
| external source unreachable | pack builds without it, flagged degraded |
| rename in delta range | old path items removed, new path indexed |

---

## 5. Invariants (MUST)

1. Index reads **bare-repo objects only** — no worktree reads, no locks.
2. Packs are immutable per task.
3. External-source tokens header-only (G-4), keyring-stored.
4. Pack sizes bounded by `ContextBudget` — no unbounded packs.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Index_BaselineKinds` | fixture repo → symbol/doc/rule items with correct kinds, paths, shas |
| 2 | `DeltaSync_RenameDeleteModifyMatrix` | each change type → exact item churn |
| 3 | `Pack_BudgetEnforced` | oversupplied matches → item + char caps honored; rules first |
| 4 | `Evidence_ShaPinned` | item sha == read-at sha; after delta sync, old task's pack unchanged |
| 5 | `RulesDigest_PinnedMidTask` | rules change → in-flight pack digest stable; next pack new digest |
| 6 | `ExternalSource_DegradedPath` | unreachable source → pack `Degraded`, no failure |
| 7 | `Index_NoWorktreeNoLocks` | spy on runner invocations: bare-repo paths only; no index.lock created |
| 8 | `BinaryHuge_Skipped` | classifier fixtures |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** indexing working trees; unbounded pack sizes; a second retrieval stack bypassing
the vault; vector-DB dependencies in v1; external tokens outside the keyring.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Vault|FullyQualifiedName~DeltaSync|FullyQualifiedName~PackBudget|FullyQualifiedName~Evidence"
grep -rn "worktrees/" GitLoom.Core/Context/        # 0 hits
grep -rn "embedding\|vector" GitLoom.Core/Context/ # 0 hits (v1)
```

---

## 8. Definition of done

- [ ] Baseline index (Roslyn/tree-sitter/docs/rules) over bare objects; delta sync on fetch + keep-alive.
- [ ] FTS5 store; budget-capped immutable packs with sha-pinned evidence + rules digest.
- [ ] Consumers wired: spawn prompt, intake draft, cockpit panel with blame/history links.
- [ ] Optional external sources with degraded path; all edge rows green.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-34**, base `phase2`.
