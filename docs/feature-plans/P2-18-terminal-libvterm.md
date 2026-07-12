# P2-18 — Terminal Target Engine: Server-Side libvterm + Skia Grid Renderer — Implementation Plan

**Task ID:** P2-18 · **Milestone:** M7 (before beta) · **Priority:** P0
**Depends on:** P2-04 green on the interim engine. **Merge gate:** P2-04 ≥ parity on libvterm —
**no golden regression**.
**Branch:** implement on `feature/P2-18-terminal-libvterm` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated (the entire P2-04 suite re-run on libvterm is the merge gate) + **human terminal-feel matrix required**.
> Conformance/replay parity, snapshot-reattach identity, and damage-coalescing ceilings are automated and gate the engine flag flip. The manual matrix (Claude Code, vim, htop, tmux driven by a human on the new engine) is required PR evidence — same v1 A.6 boundary as P2-03.
>
> **Source of truth:** §P2-18 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.1b). Any PR touching terminal code runs the P2-04 harness (global rule 3).
> P2-02's `oneof { raw; GridUpdate grid; }` output frame means this is **not** a proto break.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-18 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-18** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-18 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-03 streams raw bytes to a vendored client-side parser; P2-04 proves correctness. The target
architecture moves terminal state **server-side**: libvterm in the daemon owns the grid, the
client receives damage-rect `GridUpdate`s and renders them with a first-party Skia control. This
buys: crash recovery, reattach-with-identical-grid, thin-client streams for future cloud (P2-25),
and bounded client CPU under firehose output.

### What you can rely on

| Fact | Where |
|---|---|
| `ITerminalView` seam — swap engines without ViewModel changes | P2-03 |
| `GridUpdate` slot in the terminal proto (`oneof`) | P2-02 |
| P2-04 harness drives both engines via `ITerminalEngineHarness` | P2-04 |
| Session leader owns PTYs; daemon reattach | P2-09 |
| 16 ms ticker + pooled buffers pattern | P2-03 `TerminalStreamer` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Terminal/Vterm/VtermNative.cs` (P/Invoke: `vterm_new`, `vterm_input_write`, screen callbacks incl. `sb_pushline`/`sb_popline`, keyboard encoders) |
| **Create** | `GitLoom.Core/Terminal/Vterm/VtermSession.cs` (one per agent PTY; owned by the session leader) |
| **Create** | `GitLoom.Core/Terminal/Vterm/DamageCoalescer.cs` (damage rects → cell-run `GridUpdate`s per 16 ms tick) |
| **Create** | `GitLoom.Core/Terminal/Vterm/GridSnapshotSerializer.cs` (full grid + modes + lazy scrollback for attach/recovery) |
| **Edit** | terminal proto (`GridUpdate` message fleshed out: cell runs — UTF-32 glyph + combining, truecolor fg/bg, attr bitset; cursor; **scroll ops first-class**) |
| **Create** | `GitLoom.App/Controls/TerminalGridControl.cs` (Skia cell grid: glyph-run cache, damage-only redraw, selection/clipboard, IME overlay, CJK double-width, mouse/keyboard encoders incl. bracketed paste) — implements `ITerminalView` |
| **Create** | `build/libvterm/` (CI build of linux-x64 `libvterm.so` from pinned source; daemon-side only) |
| **Edit** | engine selection flag `TerminalEngine=libvterm|interim` (daemon + client setting) |
| **Create** | `GitLoom.Tests/Terminal/LibvtermEngineHarness.cs` (P2-04 adapter), `VtermSessionTests.cs`, `DamageCoalescerTests.cs`, `SnapshotAttachTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- P/Invoke bindings over pinned libvterm; **one `VtermSession` per agent PTY owned by the
  session leader** (survives daemon restarts like the PTY does).
- Damage rects coalesced by the 16 ms ticker into `GridUpdate` protos: **cell runs** (UTF-32
  glyph + combining chars, truecolor fg/bg, attribute bitset), cursor state, **scroll ops as
  first-class messages** (a one-line scroll is a scroll op + one damaged row, never a full-grid
  send).
- **Snapshot/attach path:** full grid + modes + lazily-fetched scrollback serving crash recovery,
  reattach, and future cloud clients.
- `TerminalGridControl`: first-party Skia cell grid — glyph-run cache keyed by (glyph, style),
  damage-only redraw, selection + clipboard, IME composition overlay, CJK double-width, mouse +
  keyboard encoders incl. bracketed paste.
- Engine behind the `TerminalEngine=libvterm|interim` flag until P2-04 signs off; linux-x64
  `libvterm.so` built in CI from pinned source, **daemon-side only** (the client never loads
  native terminal code).

---

## 3. Implementation steps

1. **CI native build:** `build/libvterm/` — pinned upstream commit, musl/glibc target matching
   the daemon publish, checksum recorded; artifact consumed by the Server publish. Windows
   `--local-dev` uses the same .so under WSL for tests or skips (document: libvterm engine is
   daemon/Linux; `--local-dev` on Windows keeps the interim engine).
2. **Bindings:** minimal surface — `vterm_new/free`, `vterm_set_utf8`, `vterm_input_write`,
   `vterm_output_read` (for keyboard encoding), screen callbacks (`damage`, `movecursor`,
   `moverect` → scroll ops, `settermprop`, `sb_pushline`/`sb_popline`), `vterm_keyboard_*`.
   Callbacks marshal into a per-session single-threaded pump (libvterm is not thread-safe —
   one session, one thread).
3. **`VtermSession`:** feeds PTY bytes → collects damage; scrollback ring (10k lines) fed by
   `sb_pushline`; exposes `DrainUpdates()` for the ticker and `Snapshot()`.
4. **`DamageCoalescer`:** merge overlapping/adjacent rects per tick; emit scroll ops before
   rect damage; convert cells to runs (consecutive same-style cells); cap per-tick payload
   (spill to next tick) so a firehose can't build unbounded frames.
5. **Snapshot/attach:** client attach (fresh, or after daemon/client crash) → `GridSnapshot`
   (grid, cursor, modes, size) then deltas; scrollback pages fetched lazily on scroll-up
   (RPC `GetScrollback(fromLine, count)`).
6. **`TerminalGridControl`:** Skia render loop — damage-only invalidation; glyph-run cache
   (measure once per (glyph,font,style)); double-width cells occupy two columns (goldens from
   P2-04 already assert width semantics); selection model (cell-rect + stream modes) + clipboard;
   IME overlay positioned at cursor; input side encodes keys/mouse/paste via vterm keyboard
   encoders (bracketed paste honored).
7. **Flag + rollout:** setting flips per profile; both engines remain runnable until P2-04
   parity, then interim is removed in a follow-up (not this PR).

---

## 4. Edge-case / performance matrix (binding)

| Case | Required behavior |
|---|---|
| kill client mid-`htop` → reattach | grid renders **identical** to pre-kill (snapshot test) |
| daemon restart with leader alive | live reattach; session continues |
| sustained 50 MB `cat` | client CPU bounded; **no full-grid sends** in steady scroll (scroll ops observed) |
| ZWJ emoji / combining marks | single cell run with combining sequence preserved |
| IME composition (CJK input) | overlay at cursor; committed text reaches PTY once |

---

## 5. Invariants (MUST)

1. P2-04 suites pass on this engine ≥ interim parity — **no golden regression** (the merge gate).
2. Scroll ops are first-class: steady scrolling produces O(changed rows) traffic.
3. Client never loads native terminal code; libvterm is daemon-side only.
4. One session = one pump thread; no cross-thread libvterm calls.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | P2-04 full suites via `LibvtermEngineHarness` | all conformance + transcripts + coverage matrix ≥ interim results |
| 2 | `Coalescer_MergesAndRuns` | overlapping rects merged; same-style cells emitted as runs; payload cap spills |
| 3 | `ScrollOps_NotFullGrid` | scripted 1000-line scroll → scroll-op messages, damaged-rows traffic only (byte budget asserted) |
| 4 | `SnapshotAttach_IdenticalGrid` | feed htop transcript → snapshot → new consumer attach → `ReadGrid()` identical |
| 5 | `Reattach_AfterDaemonRestart` (`RequiresDocker`) | leader keeps session; new daemon serves snapshot + deltas |
| 6 | `KeyboardEncoder_Matrix` | keys/modifiers/mouse/bracketed-paste → expected byte sequences (vterm encoder round-trip) |
| 7 | Damage-coalescing perf measurement | numbers pasted into the PR (frames/sec, bytes/sec under `cat` firehose) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** full-grid sends in steady scroll; client-side libvterm; goldens regressed or
regenerated to pass; thread-unsafe callback dispatch; unpinned native source.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~VtConformance|FullyQualifiedName~TranscriptReplay"   # both engines
dotnet test --filter "FullyQualifiedName~Vterm|FullyQualifiedName~DamageCoalescer|FullyQualifiedName~SnapshotAttach"
grep -rn "libvterm\|VtermNative" GitLoom.App/   # 0 hits (client renders GridUpdates only)
git diff origin/phase2 -- GitLoom.Tests/Transcripts/   # empty (no golden rewrites)
```

Manual matrix in the PR: Claude Code, vim, htop, tmux driven live on the libvterm engine.

---

## 8. Definition of done

- [ ] Pinned CI-built libvterm + bindings + per-session pump; sessions owned by the leader.
- [ ] Damage coalescing with first-class scroll ops; snapshot/attach (+ lazy scrollback).
- [ ] `TerminalGridControl` (Skia, cache, selection/clipboard/IME/CJK, encoders) behind `ITerminalView`.
- [ ] P2-04 parity green (merge gate); perf numbers + manual matrix in the PR.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-18**, base `phase2`.
