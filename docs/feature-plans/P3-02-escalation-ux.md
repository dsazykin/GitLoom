# P3-02 — Escalation UX: Plain-Language Triage — Implementation Plan

**Task ID:** P3-02 · **Milestone:** M9 · **Priority:** P0 — the most important Vibe feature
(K-3).
**Depends on:** P3-01 (checkpoints/`VerifiedGreen`), P2-26 (circuit breaker).
**Branch:** implement on `feature/P3-02-escalation-ux` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated VM/bundle/redaction tests + **human copy approval required**.
> The three actions, gating, decayed re-trip, and the zero-key-material grep are CI. The plain-language wording is the feature: the copy deck needs sign-off from a human (ideally a non-developer read-through) — 'tested against a copy deck, not developer jargon' is the master doc's bar.
>
> **Source of truth:** §P3-02 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §K-3). The wording is a product artifact: **non-technical, tested against a copy
> deck** — no developer jargon, no raw stack traces by default.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P3-02 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P3-02** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P3-02 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Design decisions (binding)** | [`VibeModeDesign.md`](../design/VibeModeDesign.md) §3 -- plain-language triage with **exactly three actions**, no raw stack traces, a "show technical details" expander, honest disabled states |

---

## 0. Context — what exists today

P2-26's circuit breaker pauses a crash-looping session and emits `[ESCALATED]`; P3-01 marks
checkpoints `VerifiedGreen` and can restore. Nothing renders the moment to a non-developer. This
task is the triage screen with exactly three actions.

### What you can rely on

| Fact | Where |
|---|---|
| Breaker trip events (trace, counts, state) + pause | P2-26 `CircuitBreaker` / `VibeOrchestrator` |
| `RestoreCheckpoint` + `VerifiedGreen` list | P3-01 |
| Transcript tail + scrollback refs | P2-37/P2-39 session state |
| Secret scrub patterns (T-30 scanner) + `RedactionExtensions` | `PreCommitScanner`, `Http/RedactionExtensions` |
| Audit events | P2-15 |
| Chat/card surface (escalation renders inside the Vibe chat too) | P2-26 chat bridge / P3-03 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Vibe/EscalationService.cs` (trip → triage model; the three actions' backend paths) |
| **Create** | `GitLoom.Core/Agents/Vibe/DiagnosticBundleBuilder.cs` (redacted bundle: transcript tail, breaker state, checkpoint list, environment summary) |
| **Create** | `GitLoom.Core/Agents/Vibe/EscalationCopy.cs` (the copy deck: keyed strings incl. repeated-escalation variants; single source for tests) |
| **Create** | `GitLoom.App/ViewModels/Vibe/TriageViewModel.cs` + `Views/Vibe/TriageView.axaml(.cs)` (three actions + "Show technical details" expander) |
| **Create** | `GitLoom.Tests/EscalationServiceTests.cs`, `DiagnosticBundleRedactionTests.cs`, `TriageGatingTests.cs`, `EscalationCopyTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

On circuit-breaker trip, a plain-language triage screen with **exactly three actions**:

1. **"Try a different approach"** — re-prompt with failure context (breaker state + last error
   class); breaker reset with a **decayed threshold** (2 identical hashes re-trip).
2. **"Go back to when it worked"** — one-click restore to the last **`VerifiedGreen`**
   checkpoint (P3-01 `RestoreCheckpoint`; journaled).
3. **"Get help"** — diagnostic bundle: recent transcript tail, breaker state, checkpoint list,
   environment summary — **redacted** (P2-01 secrets masked with the same scrub patterns as
   T-30; automated grep test on the artifact).

No raw stack traces by default ("Show technical details" expander).

---

## 3. Implementation steps

1. **`EscalationService`:** subscribes `[ESCALATED]`; builds the triage model: friendly headline
   from the error class (mapped via `EscalationCopy` — e.g. build failure / dependency problem /
   the app keeps crashing / unresolvable conflict from P3-01), the three actions with enablement
   state, technical payload (trace) behind the expander flag. Every escalation **and** every
   chosen action → audit events (`escalation_shown`, `escalation_action{action}`).
2. **Action 1 — different approach:** compose the re-prompt (failure context: breaker state
   summary + last error class + "avoid the previous approach" framing) → inject via the P2-26
   fix-prompt path → unpause → **breaker reset with decayed threshold** (the breaker takes a
   `retripThreshold: 2` state so two identical hashes re-trip; P2-26's breaker gains this
   parameterization here if not present).
3. **Action 2 — go back:** find the newest checkpoint with `VerifiedGreen == true` →
   `RestoreCheckpoint` (journaled; the restore lands **exactly** on the last checkpoint that
   preceded a green verification — invariant 3). None exists → the option renders disabled with
   the honest copy-deck explanation (edge row 1).
4. **Action 3 — get help:** `DiagnosticBundleBuilder` → zip: transcript tail (bounded),
   breaker state JSON, checkpoint list, environment summary (OS, app version, adapter+version,
   repo shape stats — no paths beyond the repo name). **Redaction pipeline:** T-30 scanner
   patterns + all known secret values (keyring key list) through `RedactionExtensions` over
   every text member **before** zipping. Bundle generation with a live PTY snapshots without
   pausing (edge row 2). Output: a file the user saves/shares (`SendUserFile`-style save dialog).
5. **Copy deck:** `EscalationCopy` holds every user-facing string; a test asserts jargon rules
   (banned terms list: "stack trace", "exception", "SIGKILL", "exit code", sha-like tokens in
   headline/body). Repeated escalations (≥2 for the same session) switch action 1's copy to
   suggest action 3 (edge row 3).
6. **Rendering:** modal-ish triage view in Vibe mode; also emitted as a chat card (P3-03
   renders it; the VM ships here). Developer-mode sessions never see triage — the breaker event
   surfaces as an ordinary agent notification (P2-13).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| no green checkpoint exists | option 2 disabled with an honest explanation |
| bundle generation with a live PTY | snapshot without pausing the session |
| repeated escalations | option 1 text changes to suggest option 3 |
| trip while triage already shown | single screen, counters updated (no stacking) |
| secrets in transcript tail | absent from the bundle (grep test) |

---

## 5. Invariants (MUST)

1. Bundle contains **zero key material** (automated grep against seeded secrets).
2. Every escalation and chosen action is an audit event.
3. Restore lands exactly on the last checkpoint that preceded a green verification.
4. All copy comes from the deck; no hardcoded strings in views.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Triage_ThreeActionsFromRealTrip` | scripted crash-looping agent (P2-26 fixture) → triage model; each action drives its backend path (spies): re-prompt+decayed reset / restore / bundle |
| 2 | `Decayed_RetripAtTwo` | after action 1, two identical traces re-trip |
| 3 | `NoGreenCheckpoint_Option2Disabled` | gating + copy string |
| 4 | `Bundle_RedactionGrep` | seeded API key + token in transcript → bundle bytes contain neither |
| 5 | `Bundle_LivePtySnapshot` | generation during streaming → session uninterrupted |
| 6 | `Copy_NoJargon` | deck strings pass the banned-terms rules |
| 7 | `RepeatedEscalation_CopySwitch` | second trip → action-1 variant |
| 8 | `Audit_EscalationEvents` | shown + action events chained |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** raw stack traces outside the expander; a fourth action; unredacted bundles;
hardcoded UI strings; triage shown to developer-mode sessions.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Escalation|FullyQualifiedName~DiagnosticBundle|FullyQualifiedName~Triage"
grep -rn "\"" GitLoom.App/Views/Vibe/TriageView.axaml | grep -iv "binding\|style\|class"   # strings come from the deck
```

---

## 8. Definition of done

- [ ] Trip → triage with exactly three actions (enablement gating, expander, repeated-escalation copy).
- [ ] Decayed breaker reset; green-checkpoint restore; redacted diagnostic bundle (grep-proven).
- [ ] Copy deck + jargon test; audit events for show + action.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P3-02**, base `phase2`.
