# P2-45 — Agent Flight Recorder — Implementation Plan

**Task ID:** P2-45 · **Milestone:** M8 · **Priority:** P2 differentiator (novel — PTY recording
indexed to commits/hunks).
**Depends on:** P2-03/P2-18 (terminal ownership), P2-39 (parsed events), P2-15 (audit
retention/redaction rules).
**Branch:** implement on `feature/P2-45-agent-flight-recorder` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-45 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **The demo:** select a hunk in review, scrub to the exact moment the agent wrote it, see the
> surrounding tool calls.

---

## 0. Context — what exists today

The daemon owns every PTY byte (P2-03/P2-18) and parses adapter tool-call events (P2-39); commits
carry timestamps and provenance. Nothing persists the terminal history past the scrollback ring.
This task records sessions (masked, retention-capped), indexes time → commit → hunk, and adds a
read-only scrubbing player wired into the review cockpit.

### What you can rely on

| Fact | Where |
|---|---|
| PTY stream fan-out tap (non-blocking pattern) | P2-26 `StreamInterceptor` / `TerminalStreamer` |
| G-13 secret field mask (apply **before** persistence) | P2-02 mask registry |
| Parsed adapter events (tool calls, plan items) with timestamps | P2-39 `AdapterEventParser` |
| Grid replay machinery (feed bytes → grid) for playback | P2-04 harness / P2-18 engine |
| Worktree commit timestamps + hunk provenance | P2-09/P2-11 |
| Audit retention/redaction policy (shared here) | P2-15 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Recorder/FlightRecorder.cs` (per-session ring-buffer → segmented persistent recording; retention caps) |
| **Create** | `GitLoom.Core/Agents/Recorder/RecordingFormat.cs` (segmented, timestamped, masked byte stream + event track; versioned container) |
| **Create** | `GitLoom.Core/Agents/Recorder/RecordingIndex.cs` (time ↔ commit ↔ hunk index built from P2-39 events + commit timestamps) |
| **Create** | `GitLoom.Core/Agents/Recorder/RecordingRetention.cs` (pruning + redaction shared with P2-15 policy) |
| **Create** | `GitLoom.App/ViewModels/Agents/FlightPlayerViewModel.cs` + `Views/Agents/FlightPlayerView.axaml(.cs)` (scrub bar, event track, read-only terminal replay) |
| **Edit** | cockpit hunk context menu — "Show the agent writing this" → player at the indexed timestamp |
| **Edit** | audit entries reference recording segment ids (`recording_ref`) |
| **Create** | `GitLoom.Tests/RecordingRoundTripTests.cs`, `RecordingIndexTests.cs`, `MaskBeforePersistTests.cs`, `RetentionPruningTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- The daemon records agent PTY streams (**ring-buffer per session, retention-capped**) indexed
  by **time → commit → hunk** (via the P2-39 event stream + worktree commit timestamps).
- Review integration: select a hunk, scrub to the exact write moment, see surrounding tool calls.
- **Replayable offline**; recordings referenced from audit entries; retention/redaction rules
  shared with P2-15.
- **Invariants:** recordings honor the **G-13 secret mask before persistence**;
  retention/redaction policy identical to audit payloads; playback is **read-only and never
  re-executes anything**.

---

## 3. Implementation steps

1. **Recording pipeline:** tap the PTY fan-out (non-blocking, same discipline as P2-26); bytes
   pass the **G-13 mask first** (mask registry patterns + active secret values from the session's
   credential set — scrub before any buffer that can persist); then into a per-session ring
   buffer flushed as **segments** (N-minute/size-capped files, timestamped frames:
   `{tOffset, bytes}`) in the daemon artifact store. A parallel **event track** stores P2-39
   parsed events (tool calls, plan items) with the same timebase.
2. **Format:** versioned container per segment (header: session, timebase, engine cols/rows,
   format version; frames; event track). Deterministic replay: feeding frames in order through
   the P2-04 "feed bytes → read grid" abstraction reproduces the terminal state at any offset
   (replay = re-feed, never re-execute).
3. **Index:** commits observed in the worktree (keep-alive/curation-aware: index by original
   commit time and re-map through P2-38's patch-id migration on rewrites) → nearest recording
   offset; hunk → commit via provenance/blame → offset. `Lookup(hunk) → (segment, tOffset,
   surrounding events)`.
4. **Player:** read-only terminal view (P2-18 grid control fed by replay) + scrub bar + event
   track lane (tool-call markers clickable → seek); speed controls; "jump to commit" markers.
   Entry points: cockpit hunk menu, session timeline (P2-37 checkpoints shown as markers too).
5. **Retention/redaction:** identical policy engine as P2-15 payloads (default 90 d; redaction
   replaces segment bytes with a tombstone, index entries survive; audit `recording_ref`s render
   tombstoned gracefully). Ring-buffer cap bounds live memory; segment store cap bounds disk
   (oldest pruned with audit note).
6. **Audit linkage:** session audit events gain `recording_ref` (segment id + offset range) —
   `audit replay` (P2-15) can cite the exact terminal moments.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| secret printed by the agent CLI | masked in the persisted segment (query proves absence) |
| hunk from a curated (rewritten) commit | index re-mapped via patch-id migration; lookup still lands |
| segment redacted | player shows tombstone for that range; adjacent ranges playable |
| daemon restart mid-session | segment sealed; new segment continues; index spans both |
| scrub during live recording | player replays sealed segments + live tail (read-only) |

---

## 5. Invariants (MUST)

1. Mask before persistence — no unmasked byte ever hits disk.
2. Retention/redaction policy identical to audit payloads.
3. Playback is read-only and never re-executes anything (replay = byte re-feed into a grid).
4. Recording tap never backpressures the terminal path.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Recording_RoundTripDeterminism` | scripted session recorded → replay to end → grid identical to live final grid (P2-04 comparison) |
| 2 | `Index_HunkToTimestamp` | fixture session (commits at known offsets) → hunk lookup lands within the write window; events surface |
| 3 | `Index_SurvivesCuration` | squash fixture → re-mapped lookups still resolve |
| 4 | `Mask_BeforePersist` | session printing a seeded secret → segment bytes contain no trace |
| 5 | `Retention_PruneAndRedact` | expiry → tombstones, index intact, audit refs render gracefully |
| 6 | `Restart_SegmentSealing` | daemon restart mid-session → both segments indexed, replay continuous |
| 7 | `Tap_NonBlocking` | slow recorder consumer → terminal stream latency unaffected (bounded channel asserted) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** persistence before masking; replay paths that execute commands; a second
retention policy; unbounded ring buffers/segment stores.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Recording|FullyQualifiedName~MaskBeforePersist|FullyQualifiedName~RetentionPruning"
grep -rn "PtyProcessShim\|Spawn" GitLoom.Core/Agents/Recorder/   # 0 hits — playback never executes
```

---

## 8. Definition of done

- [ ] Masked, segmented, retention-capped recordings with event tracks; non-blocking tap.
- [ ] Time↔commit↔hunk index (curation-surviving); cockpit "show the agent writing this".
- [ ] Read-only scrubbing player (events lane, seek, live tail); audit `recording_ref`s.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-45**, base `phase2`.
