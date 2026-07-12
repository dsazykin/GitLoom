# P2-15 — Tamper-Evident Audit Log — Implementation Plan

**Task ID:** P2-15 · **Milestone:** M7.5 — target **before 2026-08-02** (EU AI Act enforcement
window) · **Priority:** P0 for enterprise, P1 overall.
**Depends on:** P2-14 (approval records exist); start once P2-10 is merged.
**Branch:** implement on `feature/P2-15-audit-log` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated (property tests + fault injection); TSA anchor is network-gated nightly; no human step.
> Tamper sweeps, canonicalization, redaction, crash-mid-append, touchpoint coverage, and the RT-D3 gap marker are all deterministic. The RFC 3161 round-trip needs a real TSA (`RequiresNetwork`, nightly).
>
> **Source of truth:** §P2-15 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Scope split (binding):** ship the **evidence pack** first — hash chain + authorizing identity
> + `gitloomd audit verify` + the P2-16 SIEM feed. RFC 3161 external anchoring (step 3) may land
> as a fast-follow PR. Claims language is "audit-grade / tamper-evident" — never "legally
> required crypto". Security-relevant PR (global rule 4): paste check evidence.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-15 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-15** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-15 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |
| **Launch-blocker / hardening gates** | **RT-D3 (M7.5 exit):** the `killswitch_audit_gap{killEpochId, observedAt}` chained event appended on audit-store recovery -- see the 2026-07-12 additions section below |

---

## 0. Context — what exists today

G-17 already requires every agent-initiated ref mutation, spawn/kill, plan approval, and merge
decision to emit an audit event — as **plain journal rows** until now. This task upgrades the
journal to a hash-chained, encrypted, verifiable store and wires every touchpoint. The
`audit replay` extension composes these records into the enterprise-closing demo.

### What you can rely on

| Fact | Where |
|---|---|
| Existing plain audit rows at G-17 touchpoints (gateway, lifecycle, approvals, merges, egress denials, kill switch, flagged acks) | P2-08/09/10/11/14 call sites |
| Daemon SQLite + transactional writes | P2-02 infra |
| `ISecureKeyStore` for the at-rest key | P2-01 |
| Plan approval records with approver OS identity | P2-14 |
| ToS acknowledgment rows (to be chained) | P2-01 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Audit/HashChain.cs` (pure) |
| **Create** | `GitLoom.Core/Audit/AuditLog.cs` (`IAuditLog` + SQLite/file-mirror impl) |
| **Create** | `GitLoom.Core/Audit/CanonicalJson.cs` (sorted keys, invariant culture) |
| **Create** | `GitLoom.Core/Audit/AuditCrypto.cs` (AES-GCM at rest; key via `ISecureKeyStore`) |
| **Create** | `GitLoom.Core/Audit/Rfc3161Anchor.cs` (fast-follow permitted; interface + queue land now) |
| **Create** | `GitLoom.Server/Cli/AuditVerifyCommand.cs` (`gitloomd audit verify`), `AuditReplayCommand.cs` (`gitloomd audit replay <sha>`) |
| **Edit** | every G-17 touchpoint: replace plain rows with `IAuditLog.Append` (one event per operation) |
| **Create** | retention job (default 90 d) + redaction API surface |
| **Create** | `GitLoom.Tests/HashChainTests.cs`, `CanonicalJsonTests.cs`, `AuditLogTests.cs`, `AuditTouchpointCoverageTests.cs`, `AuditReplayTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// GitLoom.Core/Audit/HashChain.cs (pure)
public sealed record AuditRecord(long Seq, DateTimeOffset Timestamp, string Type, string PayloadJson, string PrevHash, string Hash);
public static class HashChain
{
    public static string ComputeHash(string prevHash, string canonicalPayload);   // SHA-256(prevHash ‖ payload)
    public static (bool Valid, long? FirstBadSeq) Verify(IEnumerable<AuditRecord> records);
}

// GitLoom.Core/Audit/AuditLog.cs (daemon)
public interface IAuditLog
{
    long Append(string type, object payload, string osIdentity);   // canonicalizes, chains, persists
    IReadOnlyList<AuditRecord> Read(long fromSeq, int take);
    (bool Valid, long? FirstBadSeq) VerifyAll();
    long Redact(long seq, string reason, string osIdentity);       // new chained event referencing the original's hash — never rewrites
}
```

**Event types (minimum):** `inference` (model, prompt, output), `agent_spawned`, `agent_stopped`,
`plan_approved`, `plan_rejected`, `merge_approved`, `merge_rejected`, `stale_override_used`,
`egress_denied`, `budget_exceeded`, `killswitch`, **`killswitch_audit_gap`** (RT-D3, M7.5 exit:
appended on audit-store recovery when a kill fired while the store was unavailable — carries
`{killEpochId, observedAt}`, so the kill switch's freeze-then-audit carve-out is made
tamper-evident rather than a silent absence), `acknowledged_flagged_change`, `redaction`.

---

## 3. Implementation steps

1. **Canonical JSON:** `CanonicalJson.Serialize(object)` — sorted keys recursively, invariant
   culture, stable number formatting, UTF-8 no-BOM. Hashing non-canonical JSON is a rejection
   trigger; every `Append` canonicalizes first.
2. **Chain + persistence:** `Append` inside one SQLite transaction: read head hash → compute →
   insert `(seq, ts, type, payload, prev, hash)` into an append-only table (no UPDATE/DELETE
   grants in code; the store API exposes none) **plus** an append-only file mirror
   (length-prefixed records; fsync'd). Crash mid-append → no torn record; chain resumes
   (transactionality test).
3. **Wire the touchpoints:** each G-17 call site emits exactly **one** event per operation
   (idempotence — retries must not double-append; use operation ids where retried). The
   `inference` events come from the P2-08 gateway (model, prompt, output payloads).
4. **Encryption + retention + redaction:** AES-GCM encrypt `PayloadJson` at rest (key in OS
   keyring via `ISecureKeyStore`, key id in the record); hash computed over the **plaintext
   canonical payload** (so verify decrypts). Retention default 90 d — expiry runs as
   **redaction events**, never row deletion (the chain stays intact). `Redact(seq, …)` appends a
   `redaction` event carrying the original's hash and replaces the stored payload with a tombstone
   — `VerifyAll` still passes (edge row 2). Full prompt/output logging is a sensitive store:
   these three properties are part of the feature, not afterthoughts.
5. **`gitloomd audit verify`:** CLI walks the chain (+ validates anchor tokens when present),
   prints head hash + first-bad-seq on failure; exit code contract (0 ok / 2 tampered).
6. **Anchoring (fast-follow permitted):** every N records / 24 h, RFC 3161 timestamp the head
   hash; store the TSA token; unreachable TSA → queued/retried — anchoring is best-effort,
   **chaining is not** (edge row 4). Land the interface + queue in this PR even if the TSA client
   ships in the follow-up.
7. **Extension — merge-decision replay:** `gitloomd audit replay <sha>` — given a commit on main,
   re-derive: the plan that approved it, the agent that produced it (P2-43 signature when
   present), the verification receipt (`VerificationRecord`), which human viewed which hunks
   (P2-38 receipts when present), chain-intact proof. Pure composition over existing records —
   a query + formatter, no new state. Fields that depend on later tasks render "not recorded"
   gracefully.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| single-byte tamper anywhere | `VerifyAll` fails at exactly that seq |
| redaction | payload replaced, chain still verifies (redaction event carries the original hash) |
| daemon crash mid-append | no torn record (transactional write); chain resumes |
| TSA unreachable | anchoring queued/retried; log keeps appending |

---

## 5. Invariants (MUST)

1. `HashChain` pure + property-tested.
2. No plaintext prompt content on disk outside the encrypted store.
3. Every G-17 touchpoint emits exactly one event (idempotence per operation).
4. No code path rewrites or deletes records — redaction is append-only.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `HashChain_Properties` | random record sequences: verify passes; any single-byte mutation (payload, prev, hash, reorder) → fails at exact seq |
| 2 | `CanonicalJson_Stability` | key order, culture (tr-TR test), number formats → identical bytes |
| 3 | `Redaction_ChainStillVerifies` | redact mid-chain → tombstone + new event + `VerifyAll` true |
| 4 | `CrashMidAppend_NoTornRecord` | kill transaction between mirror/db writes (fault injection seam) → recovery consistent |
| 5 | `TouchpointCoverage_ScriptedSession` | scripted swarm session (spawn → plan → approve → verify → merge with one override + one egress denial + killswitch) → exact expected event-type sequence |
| 6 | `Retention_RedactsNotDeletes` | expired rows → tombstones, count unchanged, chain valid |
| 7 | `EncryptionAtRest` | raw DB/file bytes contain no known plaintext marker from payloads |
| 8 | `AuditReplay_ComposesRecords` | fixture chain → replay output names plan/agent/verification for a sha; missing sources render gracefully |
| 9 | `AnchorQueue_RetriesOnFailure` (network-gated trait) | TSA failure → queued; success → token stored; verify validates it |
| 10 | `KillSwitchDuringAuditOutage_ShouldMarkGapOnRecovery` (**RT-D3, M7.5 exit — PR-blocking; the kill-side hook is P2-14's**) | kill fired while the audit store is down (fault-injection seam) → the kill is not blocked; on store recovery a **chained** `killswitch_audit_gap{killEpochId, observedAt}` is appended and `VerifyAll` passes |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** rewriting or deleting records under any code path; hashing non-canonical JSON;
plaintext payloads on disk; a touchpoint emitting zero or two events.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~HashChain|FullyQualifiedName~CanonicalJson|FullyQualifiedName~AuditLog|FullyQualifiedName~AuditTouchpoint|FullyQualifiedName~AuditReplay"
grep -rn "UPDATE audit\|DELETE FROM audit" GitLoom.Core/ GitLoom.Server/   # 0 hits
gitloomd audit verify   # exit 0 on a fresh install — paste output into the PR
```

---

## 8. Definition of done

- [ ] Pure `HashChain` + canonical JSON; append-only SQLite + file mirror; AES-GCM at rest.
- [ ] All G-17 touchpoints chained (coverage test); redaction + retention as chained events.
- [ ] `gitloomd audit verify` + `audit replay <sha>`; anchoring interface + queue (TSA client may fast-follow).
- [ ] All edge rows green; evidence pasted in the PR.
- [ ] `killswitch_audit_gap` recovery marker wired (RT-D3 — **M7.5 does not exit without guard test 10**); test contract = union of §6 and TI-P2-15.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-15**, base `phase2`.
