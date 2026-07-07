# P2-39 — Orchestration UX Pack: Message Queue, Prompt-First Dispatch, Session Search, Plan Visibility — Implementation Plan

**Task ID:** P2-39 · **Milestone:** M7.75 · **Priority:** P1-parity (Conductor queue/dispatcher,
Nimbalyst/Superset search, Codex task sidebar).
**Depends on:** P2-02, P2-13, P2-14.
**Branch:** implement on `feature/P2-39-orchestration-ux-pack` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-39 of `docs/GitLoom_Master_Implementation_Document_v2.md`. Four small,
> high-daily-value items in one task (one PR — the items share plumbing; if the diff grows
> unwieldy, split by item **with the owner's sign-off**, each PR still linking P2-39).

---

## 0. Context — what exists today

Steering a busy agent fails or interrupts; spawning requires a multi-step UI; transcripts are
unsearchable; adapters emit structured plan/subagent events nobody renders. Four gaps, one pack.

### What you can rely on

| Fact | Where |
|---|---|
| Steering channel (`send_worker_prompt`) + adapter idle/busy signals (yield protocol markers) | P2-09/P2-14 |
| T-18 command palette + `ActionRegistry` + `FuzzyMatcher` | `GitLoom.App/ViewModels/CommandPaletteViewModel.cs`, Core |
| Spawn paths: coordinator (plan approval) + manual (direct) | P2-14 |
| Daemon SQLite + FTS5 (vault set the pattern) | P2-34 |
| G-13 secret field mask (must apply before indexing) | P2-02 logging mask |
| Adapter stream-json events (Claude Code / Codex) | adapter channel (P2-22); PTY tap (P2-26 pattern) |
| Audit + flight-recorder consumers of the parsed stream | P2-15 / P2-45 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/SessionMessageQueue.cs` (per-session FIFO, persisted, reorder/cancel) |
| **Create** | `GitLoom.Core/Agents/Orchestrator/AdapterEventParser.cs` (stream-json → typed events: plan items, subagent spawns, tool calls) + per-adapter format modules |
| **Create** | `GitLoom.Core/Agents/SessionSearchIndex.cs` (FTS5 over transcripts + metadata; mask-aware ingestion; close-time summaries) |
| **Edit** | `GitLoom.App/ViewModels/CommandPaletteViewModel.cs` — "New session:" prompt-first dispatch + session-search results group |
| **Create** | `GitLoom.App/ViewModels/Agents/MessageQueueViewModel.cs` (composer "queued" state; visible/reorderable/cancellable list) |
| **Create** | `GitLoom.App/ViewModels/Agents/PlanTreeViewModel.cs` + view (live read-only plan/task tree beside the terminal) |
| **Edit** | protos (queue CRUD, search query, plan-tree stream) |
| **Create** | `GitLoom.Tests/MessageQueueTests.cs`, `PromptDispatchTests.cs`, `SessionSearchTests.cs`, `AdapterEventParserTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

1. **Message queue:** per-session FIFO in the daemon; the composer switches to "queued" while the
   adapter streams; delivered on idle; queue visible/reorderable/cancellable.
2. **Prompt-first dispatch:** the T-18 palette gains **"New session:"** — type the prompt,
   inline-pick repo + agent + base branch, Enter spawns (through plan-approval when
   coordinator-managed; direct in manual mode).
3. **Session search:** SQLite FTS5 over persisted transcripts + session metadata (titles,
   summaries auto-generated at close), palette-integrated; embeddings deliberately deferred.
4. **Plan/subagent visibility:** parse adapter structured events (Claude Code / Codex
   stream-json) into a live **read-only** plan/task tree beside the terminal; the same parsed
   stream feeds P2-15 audit and the P2-45 flight recorder.

---

## 3. Implementation steps

1. **Queue:** daemon SQLite table `(sessionId, seq, text, state: Queued|Delivering|Delivered|
   Cancelled)`; enqueue when the adapter is mid-stream (busy signal from the yield/stream state);
   drain on idle in seq order; reorder = seq swap on `Queued` rows only; cancel likewise.
   **Survives daemon restart** (invariant) — drain resumes after reconcile. Composer binds to
   the queue state ("queued (3)").
2. **Prompt-first dispatch:** palette mode triggered by typing `New session:` (or the registered
   action) → inline pickers (repo from registered set, adapter/model via P2-31 dispatcher when
   present, base branch default main) → Enter: coordinator-managed → drafts a plan for approval;
   manual mode → direct spawn (admission/budget-checked as always). Both paths unit-tested.
3. **Search:** ingestion at session close (and periodic checkpoint): transcript text passes the
   **G-13 mask first** — masked regions never reach the index (invariant + test); auto-summary
   (one bounded gateway call, budget-tagged; falls back to first-prompt truncation) + title
   stored as metadata. Palette group "Sessions" with `FuzzyMatcher`-ranked FTS hits; Enter opens
   the session (live) or its transcript view (closed).
4. **Plan tree:** `AdapterEventParser` — per-adapter module normalizing stream-json into
   `{PlanItemAdded/Updated, SubagentSpawned, ToolCallStarted/Finished}`; fixture corpus per
   adapter version (pinned via P2-22 channel). `PlanTreeViewModel` renders the live tree
   (read-only in v1 — no editing affordances); the parsed stream is also published daemon-side
   for P2-15 (`inference`-adjacent detail events) and P2-45.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| daemon restart with queued messages | queue intact, drains after restart |
| cancel while `Delivering` | too late — delivered; typed outcome tells the user |
| reorder around a delivering head | only `Queued` rows reorder |
| masked secret in transcript | absent from the FTS index (query proves it) |
| adapter emits unknown event types | parser skips gracefully, logs once, tree unaffected |
| dispatch in coordinator mode | plan approval path — never a direct spawn |

---

## 5. Invariants (MUST)

1. Queued messages survive daemon restart.
2. The search index excludes secret-masked regions (mask applied **before** indexing).
3. The plan tree is read-only in v1.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Queue_PersistOrderCancel` | enqueue 3 → restart → drain in order; cancel/reorder semantics per edge rows |
| 2 | `Queue_DeliversOnIdleOnly` | busy stream → held; idle signal → delivered (scripted adapter) |
| 3 | `Dispatch_BothModes` | coordinator → pending plan; manual → spawn with admission check (spies) |
| 4 | `Search_MaskedExcluded` | transcript with a `// SECRET` field value → FTS query for it returns nothing |
| 5 | `Search_SummaryFallback` | gateway unavailable → truncation fallback; metadata stored |
| 6 | `Parser_FixtureCorpus` | Claude Code + Codex stream-json fixtures → exact typed event sequences; unknown types skipped |
| 7 | `PlanTree_ReadOnlyProjection` | events → tree states; no mutation RPCs exist on the VM |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** queue in client memory only; indexing before masking; editable plan tree; a
per-adapter fork of the parser living in the UI.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~MessageQueue|FullyQualifiedName~PromptDispatch|FullyQualifiedName~SessionSearch|FullyQualifiedName~AdapterEventParser"
grep -rn "stream-json\|StreamJson" GitLoom.App/   # parsing stays in Core
```

---

## 8. Definition of done

- [ ] Persisted per-session FIFO with visible reorder/cancel and idle-drain.
- [ ] Palette "New session:" dispatch through both governance modes.
- [ ] Mask-aware FTS session search with close-time titles/summaries.
- [ ] Adapter event parser (fixture corpus) → live read-only plan tree + audit/recorder feeds.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-39**, base `phase2`.
