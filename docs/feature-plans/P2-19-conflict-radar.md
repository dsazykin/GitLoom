# P2-19 — Cross-Worktree Conflict Radar — Implementation Plan

**Task ID:** P2-19 · **Milestone:** M7.5 · **Priority:** P1 — a visible differentiator no
competitor ships.
**Depends on:** P2-06 (worktrees); T-02 chunker (already on main).
**Branch:** implement on `feature/P2-19-conflict-radar` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — fixture bare repos with known overlap classes; no model, screenshot, or human step.
> Warning sets, clearing, binary handling, prefilter boundedness, and read-only guarantees are all deterministic fixture tests; the radar panel rendering rides P2-13's harness.
>
> **Source of truth:** §P2-19 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`, including
> the 2026-07-07 **symbol-level radar** extension (tree-sitter). GitKraken's predictive detection
> is human-branch, line-level, post-hoc; this is live, N-way, and (with the extension) semantic.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-19 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-19** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-19 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

The daemon owns every agent worktree and the bare mirror (P2-06); `GenerateMergeChunks` (T-02) is
a pure 3-way chunker that classifies conflicts. Nothing warns that agents A and B are converging
on the same code *before* either merges. This task scans pairwise (and each-vs-main) after every
keep-alive cycle and surfaces warnings in the UI and the queue.

### What you can rely on

| Fact | Where |
|---|---|
| Pure 3-way chunker `GenerateMergeChunks(base, ours, theirs)` with `Conflict` chunk class | T-02, `GitLoom.Core` merge-chunker surface |
| Bare mirror + `agent/<id>` branches; blob reads without touching worktrees | P2-06 |
| Keep-alive cadence hook (post-cycle event) | P2-09 `KeepAliveRebaser` |
| Agent cards + badges (P2-13), queue panel (P2-10) | UI surfaces to extend |
| Hardened git runner in the daemon | P2-06 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/ConflictRadar.cs` (`IConflictRadar` + impl) |
| **Create** | `GitLoom.Core/Agents/RadarClassifier.cs` (pure: per-path blob triple → overlap verdict; separated from git plumbing) |
| **Create** | `GitLoom.Core/Agents/SymbolOverlap.cs` (extension: tree-sitter parse of touched ranges → symbol names) + native tree-sitter packaging (pinned grammars: C#, TS/JS, Python to start) |
| **Edit** | `GitLoom.Server` scheduling (post-keep-alive hook) + proto (radar warnings stream/query) |
| **Edit** | `GitLoom.App/ViewModels/Agents/AgentCardViewModel.cs` (overlap badge), `MergeQueueViewModel.cs` ("merging A will conflict B" hint) |
| **Create** | `GitLoom.App/ViewModels/Agents/ConflictRadarPanelViewModel.cs` + view (pairs/paths/symbols list) |
| **Create** | `GitLoom.Tests/ConflictRadarTests.cs`, `RadarClassifierTests.cs`, `SymbolOverlapTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// daemon GitLoom.Core/Agents/ConflictRadar.cs
public sealed record OverlapWarning(string AgentA, string AgentB, string Path, bool CertainConflict); // certain = same-line
public interface IConflictRadar
{
    /// <summary>Pairwise diff of live agent branches (and each vs main): file-level overlap
    /// plus line-level certainty via the T-02 chunker on the overlapping files.</summary>
    IReadOnlyList<OverlapWarning> Scan(string repoHash);
    event Action<OverlapWarning>? NewOverlap;    // raised by the scheduled scan on new findings
}
```

(`AgentB == "main"` for branch-vs-main overlap rows; symbol extension adds an optional
`Symbol` field on the warning — additive, keep the record's binding shape.)

---

## 3. Implementation steps

1. **Name-only prefilter (step 1):** per live branch, `git diff --name-only main...agent/<id>`
   (CLI against the bare repo). File-set intersection per pair → candidate paths only. Running
   the chunker without this prefilter is a rejection trigger.
2. **Chunk classification (step 2):** for each candidate `(pair, path)`: read the three blobs
   (merge-base, A's tip, B's tip) from the **bare repo** (`git show`/object reads — never
   worktree files) → `GenerateMergeChunks`. Any `Conflict` chunk ⇒ `CertainConflict=true`; same
   file, disjoint chunks ⇒ soft warning. Binary blobs → file-level warning only, never chunk
   classification (edge row 1). `RadarClassifier` holds this logic **pure** (blob texts in,
   verdict out) for unit tests.
3. **Scheduling (step 3):** subscribe to P2-09's post-keep-alive event — piggyback the cadence,
   **no extra yields** (the radar reads refs/blobs only; it never touches worktrees or takes the
   index lock). Debounce: one scan per repo per cycle. Diff results per pair cached by
   `(shaA, shaB)` — unchanged tips skip work.
4. **Surfacing (step 4):** warning set diffed against the previous scan → `NewOverlap` events;
   cleared warnings retract (edge row 4). UI: badge on both agent cards, radar panel
   (pair → paths → certainty/symbols), queue hint ("merging A will conflict B") next to the
   merge button.
5. **Symbol extension:** for text candidates in supported languages, parse both tips with
   tree-sitter (pinned grammar builds, daemon-side native like libvterm's pattern), map each
   branch's changed line ranges → enclosing function/type nodes; intersection of symbol sets ⇒
   warning gains `Symbol` ("both editing `AuthService.Login`"). Unsupported language → line-level
   behavior unchanged (graceful degrade).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| binary file overlap | file-level warning only, never chunk classification |
| agent branch identical to main | no self-noise (zero warnings) |
| 6 agents (15 pairs) on a large repo | scan bounded: name-only diffs first, chunker only on intersections; measured in the PR |
| overlap disappears after a rebase | warning cleared |
| unsupported language file | line-level warning without symbol info |

---

## 5. Invariants (MUST)

1. Radar is **read-only** — never touches worktrees or locks the index (bare-repo object reads
   only).
2. Pure classification logic separated from git plumbing for unit tests.
3. Piggybacks the keep-alive cadence — no extra yield cycles introduced.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Radar_ThreeBranchFixture` | fixture bare repo: branch pair with same-line edits → `CertainConflict`; same-file disjoint → soft; no overlap → absent. Exact warning set |
| 2 | `Radar_VsMain` | branch overlapping main's recent commit → warning with `AgentB == "main"` |
| 3 | `Radar_ClearsAfterRebase` | rebase the branch past the overlap → next scan retracts |
| 4 | `Radar_BinaryFileLevelOnly` | binary blob pair → file warning, chunker never invoked (spy) |
| 5 | `Radar_IdenticalToMain_NoNoise` | fresh branch == main → zero warnings |
| 6 | `Classifier_PureFixtures` | blob triples → verdicts, no I/O |
| 7 | `Prefilter_BoundsWork` | 6-branch fixture → chunker invocation count == candidate intersections only; scan time measured (report in PR) |
| 8 | `SymbolOverlap_CSharpFixture` | both branches edit the same method → symbol named; different methods in same file → disjoint symbols, soft warning |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** scanning working trees directly; running the chunker on every pair×file without
the name-only prefilter; radar taking yields/locks; symbol parsing on the UI side.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~ConflictRadar|FullyQualifiedName~RadarClassifier|FullyQualifiedName~SymbolOverlap"
grep -rn "worktrees/" GitLoom.Core/Agents/ConflictRadar.cs   # 0 hits — bare repo only
```

---

## 8. Definition of done

- [ ] `IConflictRadar` per contract: prefilter → chunker → warning set; events on new/cleared.
- [ ] Read-only bare-repo scanning on the keep-alive cadence with pair caching; bounded-scan measurement in the PR.
- [ ] Badges + radar panel + queue hint; symbol-level warnings for C#/TS/Python.
- [ ] All edge rows tested. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-19**, base `phase2`.
