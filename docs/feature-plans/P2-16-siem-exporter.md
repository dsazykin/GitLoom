# P2-16 — SIEM Exporter — Implementation Plan

**Task ID:** P2-16 · **Milestone:** M7.5 · **Priority:** P1 (enterprise)
**Depends on:** P2-15 (the audit event stream).
**Branch:** implement on `feature/P2-16-siem-exporter` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-16 of `docs/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §H-8.3). Ships in the same milestone as P2-15 for the 2026-08-02 window.

---

## 0. Context — what exists today

P2-15 produces a hash-chained audit stream. Enterprises need it in their SIEM. This task streams
those events out over the three standard paths (syslog CEF/JSON over TCP/TLS, Splunk HEC, generic
webhook) with buffering and delivery-state visibility.

### What you can rely on

| Fact | Where |
|---|---|
| `IAuditLog.Read(fromSeq, take)` + append notifications | `GitLoom.Core/Audit/AuditLog.cs` (P2-15) |
| Event taxonomy (13+ types) | P2-15 §2 |
| Daemon SQLite for sink cursors; secrets via `ISecureKeyStore` (HEC tokens, TLS material refs) | P2-01/P2-02 |
| Settings UI patterns | `GitLoom.App/ViewModels/` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Audit/Export/SiemExporter.cs` (fan-out engine: per-sink cursor, bounded queue, retry) |
| **Create** | `GitLoom.Core/Audit/Export/ISiemSink.cs` + `SyslogSink.cs` (TCP/TLS, CEF + JSON framing), `SplunkHecSink.cs`, `WebhookSink.cs` |
| **Create** | `GitLoom.Core/Audit/Export/CefFormatter.cs`, `SiemJsonFormatter.cs` (pure) |
| **Create** | `docs/siem-events.md` (event taxonomy documentation) |
| **Create** | `GitLoom.App/ViewModels/SiemSinksViewModel.cs` + view (per-sink config + delivery-status panel) |
| **Create** | `GitLoom.Tests/CefFormatterTests.cs`, `SiemJsonSchemaTests.cs`, `SiemExporterBufferTests.cs`, `GitLoom.Tests/Integration/SiemSinkIntegrationTests.cs` (local syslog container + mock HEC, `RequiresDocker`) |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

`SiemExporter` streams P2-15 events as **CEF and JSON over syslog (TCP/TLS)**, **Splunk HEC**,
and a **generic webhook**; per-sink configuration; buffering + retry with a **bounded queue**;
a delivery-status panel; the event taxonomy documented in `docs/siem-events.md`.

---

## 3. Implementation steps

1. **Formatters (pure):** `CefFormatter` — `CEF:0|GitLoom|GitLoom|<ver>|<type>|<name>|<sev>|ext`
   with escaping per the CEF spec (pipe/backslash/equals); severity mapping table by event type
   (killswitch/stale_override high, inference low). `SiemJsonFormatter` — stable envelope
   `{seq, timestamp, type, identity, payload, hash, prevHash}`; a **JSON schema** checked into
   the repo validates every emitted document (schema test). Payloads pass through **redaction
   state as-is** (tombstoned events export tombstones — never decrypt-and-leak more than the
   audit UI shows).
2. **Exporter engine:** subscribes to append notifications; per-sink durable cursor (`lastSeq`
   in SQLite) → at-least-once delivery, in-order per sink; bounded in-memory queue (default
   10k events) fed from the cursor — on overflow the queue drops to cursor-backfill mode (the
   store is the buffer; nothing is lost up to retention) and the sink state goes **loud**
   (`Degraded/Backfilling` in the panel).
3. **Sinks:** `SyslogSink` — TCP with optional TLS (`SslStream`, server cert validation, opt-in
   CA pin), RFC 5424 framing (octet-counted); `SplunkHecSink` — HTTPS POST batches, token from
   keyring, 429/5xx backoff; `WebhookSink` — HTTPS POST JSON array batches, HMAC signature header
   (shared secret from keyring). All secrets via `ISecureKeyStore`; never in config files (G-13).
4. **Retry:** exponential backoff per sink, jitter, cap; a failing sink never blocks other sinks
   (independent pumps) or the audit log itself.
5. **UI:** sink list (add/edit/test-connection button, enable/disable), live delivery state
   (Connected / Degraded / Backfilling / Failed, lag = head seq − cursor). Design tokens.
6. **`docs/siem-events.md`:** one section per event type: fields, example CEF line, example JSON,
   severity.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| sink outage mid-stream | buffered redelivery on recovery; zero loss up to the cap; loud state past it |
| malformed sink config (bad host/token) | typed validation at save; test-connection surfaces the error |
| slow sink + fast producer | bounded memory (queue cap + cursor backfill); other sinks unaffected |
| redacted event | exported as tombstone, never original payload |
| 1k events/min sustained | all sinks keep up in the load test; no unbounded growth |

---

## 5. Invariants (MUST)

1. Zero event loss up to the buffer cap; loud state past it (never silent gaps — cursor
   guarantees at-least-once).
2. Every JSON document validates against the checked-in schema.
3. 1k events/min load test passes.
4. Sink credentials only in the keyring.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Cef_EscapingAndSeverityMatrix` | fixture events per type → exact CEF lines (escaping corpus) |
| 2 | `Json_SchemaValidationCorpus` | every event type → schema-valid document |
| 3 | `Exporter_OutageRedelivery` | sink down for N events → recovery replays from cursor, in order, no duplicates beyond at-least-once bounds |
| 4 | `Exporter_BoundedQueue_Backfill` | overflow → backfill mode + loud state; memory bounded |
| 5 | `Sink_Independence` | one failing sink → others deliver normally |
| 6 | `LoadTest_1kPerMin` | sustained rate → lag bounded (tagged slow) |
| 7 | `SyslogAndHec_Integration` (`RequiresDocker`) | local syslog container receives framed CEF; mock HEC receives batches with token header |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** unbounded buffering; silent event loss; secrets in config files; a sink blocking
the audit append path.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Cef|FullyQualifiedName~SiemJson|FullyQualifiedName~SiemExporter"
dotnet test --filter "Category=RequiresDocker&FullyQualifiedName~SiemSink"
grep -rn "token\|secret" GitLoom.Core/Audit/Export/ | grep -i "config\|json"   # keyring only
```

---

## 8. Definition of done

- [ ] Three sinks (syslog TCP/TLS CEF+JSON, Splunk HEC, webhook) with per-sink cursors, bounded queue, independent retry.
- [ ] Pure formatters + JSON schema; `docs/siem-events.md` complete.
- [ ] Delivery-status panel; outage/redelivery + load tests green.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-16**, base `phase2`.
