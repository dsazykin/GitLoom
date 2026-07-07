# P2-44 — Sandbox Health & Exfiltration Panel — Implementation Plan

**Task ID:** P2-44 · **Milestone:** M7.75 · **Priority:** P1 (novel — extends P2-07/P2-17).
**Depends on:** P2-07 (egress telemetry), P2-17 (transparency view).
**Branch:** implement on `feature/P2-44-sandbox-health-panel` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-44 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Thesis:** verifiable trust as a visible daily feature, not a whitepaper — "your agent tried
> to POST to pastebin at 14:02."

---

## 0. Context — what exists today

P2-07 logs egress verdicts; P2-17 streams raw connections into the transparency view; P2-06 logs
quarantine-remote pushes. Nothing aggregates these into per-agent security telemetry with alert
semantics. This task adds three new signal sources (secret-file access, anomalous process spawns,
push events) and the per-agent health strip + drill-down panel.

### What you can rely on

| Fact | Where |
|---|---|
| Denied/allowed egress events with agent attribution | P2-07 proxy logs / P2-17 `EgressLogStream` |
| Credentials tmpfs per agent (`/run/secrets`, 0400) | P2-07 |
| Container exec/process visibility (Docker events API) | P2-07 `SandboxEngine` |
| Quarantine-remote push events | P2-06 bare-repo hooks (`post-receive` on the mirror) |
| Audit chain + webhook routing | P2-15 / P2-32 |
| Agent cards (health strip surface) | P2-13 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Sandbox/SecurityTelemetry.cs` (aggregator: typed `SecuritySignal` stream per agent) |
| **Create** | `GitLoom.Core/Agents/Sandbox/SecretAccessMonitor.cs` (tmpfs access auditing — inotify/fanotify watch on `/run/secrets` inside the VM, read-only observation) |
| **Create** | `GitLoom.Core/Agents/Sandbox/ProcessSpawnMonitor.cs` (Docker events/exec + in-container process policy list → anomaly signals) |
| **Create** | `GitLoom.Core/Agents/Sandbox/PushEventMonitor.cs` (mirror `post-receive` hook → push events with ref/agent) |
| **Edit** | proto: `SecurityService.StreamSignals(agentId?)` |
| **Create** | `GitLoom.App/ViewModels/Agents/SandboxHealthViewModel.cs` (per-agent strip) + `SecurityPanelViewModel.cs` (drill-down) + views |
| **Edit** | P2-13 `AgentCardViewModel` (health strip chip), P2-32 event routing (alert notifications) |
| **Create** | `GitLoom.Tests/SecurityTelemetryTests.cs`, `SignalPanelProjectionTests.cs`, `SignalAuditEmissionTests.cs`, `AlertRoutingTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

Live per-agent security telemetry as a first-class UI:

- **blocked egress attempts** (destination, process, time),
- **secret-file access attempts** (tmpfs audit hooks),
- **anomalous process spawns** (policy list),
- **quarantine-remote push events** (P2-06),

each **streamed to the audit chain** and rendered as a per-agent **health strip** + drill-down
panel.

**Invariants:** telemetry read path is read-only over proxy/daemon logs; **alerts are events,
never auto-kills** (the human/kill-switch decides); zero PII beyond what the audit chain already
carries.

---

## 3. Implementation steps

1. **Signal model:** `SecuritySignal { AgentId, Kind: EgressDenied|SecretAccess|ProcessAnomaly|
   QuarantinePush, Severity, When, Detail (dest/process/ref) }`. `SecurityTelemetry` merges the
   four monitors into one stream; every signal → audit event (`security_signal` typed payloads —
   `egress_denied` already exists from P2-15's taxonomy; reuse it for that kind, don't
   double-emit).
2. **Egress source:** subscribe the P2-17 log stream, filter verdict `Denied` (+ correlate the
   in-container source process where the proxy log carries the connection origin; else omit —
   never guess).
3. **`SecretAccessMonitor`:** VM-side watcher (inotify on the per-agent `/run/secrets` mount
   from the daemon's namespace visibility) recording open/read events beyond the expected
   adapter-launch read window; observation only — no blocking (invariant: read-only path).
   Expected-baseline config (first read at launch = normal; subsequent reads = signal, severity
   informational; reads by unexpected binaries = warning).
4. **`ProcessSpawnMonitor`:** Docker events (`exec_create`, top sampling on a coarse timer) vs a
   policy list (expected: adapter CLI, devbox toolchain, shells, git; configurable additions).
   Unknown binaries → `ProcessAnomaly` (informational→warning per list policy). No auto-kill —
   ever (invariant 2).
5. **`PushEventMonitor`:** `post-receive` hook installed in the bare mirror (P2-06 template)
   writes push records (agent branch, ref, old/new sha) to a spool the daemon tails → signals
   (normal pushes informational — the panel's value is the *stream*, quarantine violations are
   structurally impossible but attempted-force-to-main patterns still show).
6. **UI:** health strip on the agent card (traffic-light + last-signal age); drill-down panel:
   per-agent timeline of signals with filters, each row expandable ("tried to POST to
   pastebin.com at 14:02 — blocked by egress policy", process, destination); export defers to
   the P2-17 view for raw connections (link, don't duplicate).
7. **Alert routing:** warning+ signals → P2-32 webhook/chat routing (per-event-type config) and
   the OS notification path (P2-13 suppression rules apply).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| burst of denied egress (loop) | signals coalesced per (dest, minute); panel readable; audit complete |
| signal for a torn-down agent | attributed historically; strip absent, panel retains timeline |
| monitor source unavailable (no inotify) | degraded chip on the strip; other sources unaffected |
| unknown process that is later allowlisted | past signals stand (audit immutable); new spawns clean |
| proxy log without process attribution | signal without process field — never guessed |

---

## 5. Invariants (MUST)

1. Telemetry read path is read-only over proxy/daemon logs — monitors never block or modify.
2. Alerts are events, never auto-kills.
3. Zero PII beyond what the audit chain already carries.
4. Every signal lands exactly once in the audit chain (reuse `egress_denied` where it exists).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Telemetry_MergesFixtureStreams` | four fixture sources → ordered merged signal stream per agent |
| 2 | `Panel_ProjectionStates` | fixture signals → strip severity + drill-down rows/filters |
| 3 | `Signals_AuditEmission` | each kind → exactly one chained event; egress kind reuses `egress_denied` |
| 4 | `Burst_Coalescing` | 100 denials same dest/minute → coalesced signal with count |
| 5 | `Alert_RoutingWarnings` | warning signal → webhook payload (schema) + notification (suppression honored) |
| 6 | `Degraded_SourceUnavailable` | monitor throwing at start → degraded state, others live |
| 7 | `NoAutoKill_Static` | no `Kill`/`Stop` call sites in monitor code (grep test) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** monitors with side effects (blocking/killing); double audit emission; PII fields;
duplicating the P2-17 raw view.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~SecurityTelemetry|FullyQualifiedName~SignalPanel|FullyQualifiedName~SignalAudit|FullyQualifiedName~AlertRouting"
grep -rn "StopAsync\|Kill" GitLoom.Core/Agents/Sandbox/SecretAccessMonitor.cs GitLoom.Core/Agents/Sandbox/ProcessSpawnMonitor.cs   # 0 hits
```

---

## 8. Definition of done

- [ ] Four signal sources merged into per-agent telemetry, audit-chained once each.
- [ ] Health strip + drill-down panel (coalescing, filters, historical retention); degraded-source handling.
- [ ] Warning+ alerts through webhooks/notifications; no auto-kill anywhere.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-44**, base `phase2`.
