# P2-26 — `VibeOrchestrator` Engine + Stream Interception — Implementation Plan

**Task ID:** P2-26 · **Milestone:** M8 · **Priority:** P1 (shared architecture — the Coordinator
reuses it)
**Depends on:** P2-03 (PTY streams), P2-08 (gateway/pause plumbing).
**Branch:** implement on `feature/P2-26-vibe-orchestrator-engine` off `phase2`; PR targets
`phase2`.

> **Verification profile:** Automated (recorded-transcript corpus + breaker math + scripted dev server) + **real-adapter model smoke before ship**.
> Pattern matching runs against checked-in real transcripts (vite/next/dotnet, ANSI intact); the breaker is pure. Before ship, tap one real agent CLI session to confirm the interception patterns hold against current adapter output formats (model testing; transcripts then get re-recorded as fixtures).
>
> **Source of truth:** §P2-26 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §K-1). **Scope fence:** K-2…K-5 (auto-checkpoints, escalation UX, Vibe UI, one-click
> deploy) are P3-01…P3-04 — engine only here; no Vibe UI ships in this task.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-26 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-26** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-26 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

The daemon owns every agent-CLI PTY stream (P2-03) and, once P2-33 lands, dev-server PTYs too.
The Vibe product (and today's Coordinator) needs structured signals out of those byte streams:
"the app is up on port X", "the CLI is asking for OAuth", "the dev server is crash-looping".
This task builds the in-memory stream tap that turns raw PTY bytes into those events, plus the
self-healing loop (error → fix prompt) and its circuit breaker.

### What you can rely on

| Fact | Where |
|---|---|
| PTY byte streams flow through `TerminalStreamer` (a tap point exists daemon-side) | P2-03 |
| Pause/resume + `docker pause` plumbing | P2-08/P2-09 |
| Loopback OAuth flow keyed by `state` | P2-22 `LoopbackOAuthListener` |
| Chat bridge RPC surface pattern | P2-14 coordinator protos |
| ANSI stripping/VT parsing utilities | P2-03/P2-04 terminal stack |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Vibe/StreamInterceptor.cs` (in-memory tap: pattern pipeline over ANSI-stripped lines) |
| **Create** | `GitLoom.Core/Agents/Vibe/StreamPatterns.cs` (pure matchers: port harvest, OAuth URL, error signatures) |
| **Create** | `GitLoom.Core/Agents/Vibe/SelfHealLoop.cs` (error → fix prompt into agent stdin) |
| **Create** | `GitLoom.Core/Agents/Vibe/CircuitBreaker.cs` (pure: trace-hash counting + thresholds) |
| **Create** | `GitLoom.Core/Agents/Vibe/VibeOrchestrator.cs` (composition: taps per session, event emission, escalation hook) |
| **Edit** | proto: `VibeService` chat-bridge RPC + orchestrator event stream (`[APP_READY_ON_PORT_X]`, `[AUTH_REQUIRED]`, `[ESCALATED]`) |
| **Create** | `GitLoom.Tests/StreamPatternsTests.cs`, `CircuitBreakerTests.cs`, `SelfHealLoopTests.cs`, `GitLoom.Tests/Integration/CrashingDevServerTests.cs` (`RequiresDocker`) |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

Daemon service tapping agent-CLI + dev-server PTY streams **in memory**:

- **Dev-server port harvesting:** `http://localhost:(\d+)` → emit `[APP_READY_ON_PORT_X]`.
- **OAuth-URL detection** → `[AUTH_REQUIRED]` with `state=<agent_uuid>` (routes into the P2-22
  loopback flow).
- **Error interception:** `ERR!` / stack traces → a fix prompt written into agent stdin — the
  **bytes never leave the VM**.
- **Circuit breaker:** SHA-256 of the **normalized** trace; **3 identical** traces or **5 errors
  / 10 min** → `docker pause` + escalate.
- **Chat bridge RPC** (the Vibe UI's future entry point; the Coordinator can drive it today).

---

## 3. Implementation steps

1. **Tap:** `StreamInterceptor` subscribes to the PTY stream fan-out (same source as
   `TerminalStreamer`, non-blocking — a slow matcher never backpressures the terminal path;
   bounded channel, drop-oldest for analysis purposes only). Line assembler over ANSI-stripped
   text (strip via the VT tooling — matchers see plain text; raw bytes are never regexed).
2. **Matchers (pure, `StreamPatterns`):**
   - Port: `https?://(?:localhost|127\.0\.0\.1):(\d+)` + common framework banners (vite/next
     "ready" lines) → `AppReady(port)` (dedupe per session until port changes).
   - OAuth: URLs on known authorize endpoints or `code=`-shaped prompts from CLIs →
     `AuthRequired(url)`; the orchestrator rewrites/annotates with `state=<agent_uuid>` and
     hands to the P2-22 flow.
   - Errors: `npm ERR!`, node/python/dotnet stack-trace shapes, `ECONNREFUSED`, build-failure
     footers → `ErrorTrace(text)` with capture window (N lines around the trigger).
3. **Normalization + breaker:** normalize traces (strip timestamps, paths→basenames, line
   numbers, addresses/hex, durations) → SHA-256. `CircuitBreaker.Record(hash, now)` — pure state:
   3 identical hashes, or 5 error events in a rolling 10-minute window → `Trip`. On trip:
   `docker pause` the session + emit `[ESCALATED]` with the trace + counts (P3-02 renders it
   later; today it lands in agent events + audit).
4. **`SelfHealLoop`:** on `ErrorTrace` (breaker not tripped): compose a bounded fix prompt
   (template + trace excerpt) and write it to the **agent CLI stdin** (not the dev server);
   in-VM only — the prompt content and trace bytes never leave over any RPC other than the
   normal terminal stream the user already sees. Cooldown between injections (no prompt storms);
   every injection audited (`inference`-adjacent event type `selfheal_prompt`).
5. **Chat bridge:** `VibeService.Chat` bidi RPC — text in → agent stdin; structured events out
   (ready/auth/escalated). The Coordinator (P2-14) may consume the same events for its telemetry.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| port URL split across PTY frames | line assembler reunites; single `AppReady` |
| same error with different timestamps/paths | normalizes to the same hash → breaker counts it |
| 3 identical crashes | `docker pause` + `[ESCALATED]` (not a 4th injection) |
| interleaved distinct errors (5 in 10 min) | breaker trips on volume |
| ANSI-colored error output | matchers see stripped text; still matched |
| slow matcher pipeline | terminal stream unaffected (non-blocking tap) |

---

## 5. Invariants (MUST)

1. Interception is in-memory in the VM; **error bytes/fix prompts never leave the VM** beyond the
   ordinary terminal stream.
2. Matchers + breaker are pure and tested against recorded transcripts (ANSI stripped).
3. The tap never backpressures or reorders the terminal path.
4. Every self-heal injection and escalation is audited.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Patterns_RecordedTranscripts` | recorded vite/next/npm-err/py-traceback transcripts → exact event sequences |
| 2 | `Patterns_SplitFrames` | port URL split at every offset (reuse the P2-04 splitting discipline) → one event |
| 3 | `Normalization_HashStability` | same trace, varied timestamps/paths/addresses → equal hashes; different root error → different |
| 4 | `Breaker_Math` | 3-identical and 5-in-10min rules (virtual clock); reset window behavior |
| 5 | `SelfHeal_InjectsWithCooldown` | error → one stdin injection; second error inside cooldown → deferred |
| 6 | `CrashingDevServer_EndToEnd` (`RequiresDocker`) | scripted crash-looping server → 2 heal attempts → breaker trips → container paused + `[ESCALATED]` |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** regexing raw bytes (must strip ANSI first); blocking tap; error content in RPC
payloads beyond the terminal stream/escalation event; unbounded injection loops (missing breaker
or cooldown).

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~StreamPatterns|FullyQualifiedName~CircuitBreaker|FullyQualifiedName~SelfHeal"
dotnet test --filter "Category=RequiresDocker&FullyQualifiedName~CrashingDevServer"
```

---

## 8. Definition of done

- [ ] Non-blocking in-memory tap + pure matchers (port/OAuth/error) over stripped text.
- [ ] Self-heal loop with cooldown + audit; breaker (3-identical / 5-per-10min) → pause + escalate.
- [ ] Chat bridge RPC; events consumable by Coordinator today, Vibe UI later.
- [ ] Recorded-transcript + crashing-server integration green.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-26**, base `phase2`.
