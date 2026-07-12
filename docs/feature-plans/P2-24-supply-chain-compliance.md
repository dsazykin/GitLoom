# P2-24 — Supply-Chain & Secrets Compliance — Implementation Plan

**Task ID:** P2-24 · **Milestone:** M8 · **Priority:** P2
**Depends on:** P2-10 (the `Verified` gate to hang the SCA check on), P2-01 (`ISecureKeyStore`).
**Branch:** implement on `feature/P2-24-supply-chain-compliance` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — per-ecosystem fixtures + dev-mode Vault container (`RequiresDocker`); no human step.
>
> **Source of truth:** §P2-24 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §H-8.5). Reuses P2-11's lockfile parsing where it exists — do not build a second
> lockfile-delta extractor if `LockfileSemanticDiff` already covers the ecosystem.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-24 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-24** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-24 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-01 defined `ISecureKeyStore` with the OS keyring as the only backend; P2-11 parses lockfile
deltas for the review cockpit. Enterprises need (a) centralized secret backends (Vault, AWS) and
(b) a license/SCA gate: an agent branch that introduces a copyleft dependency must be flagged and
block the merge button until acknowledged.

### What you can rely on

| Fact | Where |
|---|---|
| `ISecureKeyStore` (Set/Get/Delete) with `SecureKeyring` impl | P2-01 |
| `LockfileSemanticDiff` (npm/pnpm/csproj/poetry deltas + OSV) | P2-11 |
| Flagged-changes panel + acknowledgment gate (`IMergeGate`) | P2-11 |
| `Verified` state transition point (post-verification hook) | P2-10 `MergeQueue` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Security/Backends/VaultKeyStore.cs` (Vault KV2), `AwsSecretsManagerKeyStore.cs` — both `ISecureKeyStore` |
| **Create** | `GitLoom.Core/Security/KeyStoreSelector.cs` (org policy → backend; local keyring default) |
| **Create** | `GitLoom.Core/Compliance/SpdxLicenseDb.cs` (local SPDX id database + package→license lookup tables per ecosystem) |
| **Create** | `GitLoom.Core/Compliance/LicenseGate.cs` (lockfile delta → license findings; copyleft heuristics) |
| **Edit** | `GitLoom.Core/Agents/Orchestrator/MergeQueue.cs` — run the SCA/license gate at the `Verified` transition; findings feed the P2-11 flagged panel as a blocking category |
| **Edit** | settings UI: backend selection + Vault/AWS connection config (secrets for the backends themselves stay in the local keyring — bootstrap trust) |
| **Create** | `GitLoom.Tests/VaultKeyStoreTests.cs` (dev-mode container, `RequiresDocker`), `AwsKeyStoreTests.cs` (localstack or recorded seam), `LicenseGateTests.cs`, `LockfileDeltaEcosystemTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **Vault KV2 + AWS Secrets Manager backends for `ISecureKeyStore`**, selectable per org policy.
- **SCA/license gate at `Verified`:** lockfile-delta extraction (npm/pnpm/NuGet) → SPDX license
  lookup (**local database** — no network at gate time) → copyleft heuristics flag **GPL/AGPL as
  a blocking review category** in the P2-11 flagged panel.

---

## 3. Implementation steps

1. **Backends:** `VaultKeyStore` — KV2 `data/<mount>/gitloom/<key>` read/write/delete via the
   HTTP API, token/approle auth from the local keyring; typed failures (sealed vault, permission
   denied). `AwsSecretsManagerKeyStore` — `gitloom/<key>` secrets via the AWS SDK, standard
   credential chain; both behind the exact `ISecureKeyStore` shape (no interface growth).
   Backend selection in `KeyStoreSelector` from settings/policy (P2-23 policy doc may set it);
   fallback + migration helper (copy keys local→backend on switch, then delete local after
   verify).
2. **License DB:** ship an SPDX license-list snapshot + per-ecosystem package→license source:
   npm/pnpm lockfiles carry no license — resolve via the package's `package.json` inside the
   worktree's `node_modules` (present post-verification install) or a bundled top-N mapping;
   NuGet: `.nuspec` license expression from the package cache. Document precision honestly:
   unknown → `Unknown` finding (reviewable, non-blocking by default, configurable).
3. **`LicenseGate`:** input = lockfile delta (reuse `LockfileSemanticDiff` outputs; extend it for
   any missing ecosystem rather than duplicating) → per-added/updated package: license id →
   classification `{Permissive, WeakCopyleft, StrongCopyleft(GPL/AGPL), Unknown}` → findings.
   Copyleft heuristics include expression parsing (`MIT OR GPL-2.0` → permissive path available →
   flag as reviewable, not blocking; pure `GPL-3.0-only`/`AGPL-*` → blocking).
4. **Gate wiring:** on `Verifying → Verified`, run the gate against the branch's merge diff;
   `StrongCopyleft` findings register as flagged items (category `SecuritySensitivePath`-tier
   blocking — add a `License` risk category if cleaner) requiring item-by-item acknowledgment
   before `CanMerge` (invariant: an AGPL-introducing branch blocks the merge button until
   acknowledged).
5. **UI:** findings render in the flagged panel with package, version jump, license, and the
   classification reason.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| AGPL package added | blocking flagged item; merge blocked until acknowledged |
| dual-licensed `MIT OR GPL-2.0` | reviewable finding, not blocking |
| unknown license | `Unknown` finding, configurable severity, default non-blocking |
| Vault sealed / AWS denied | typed failure; keys never silently fall back to plaintext |
| backend switch | migration copies + verifies before deleting local copies |
| lockfile removed entirely | gate no-ops with a note (no crash) |

---

## 5. Invariants (MUST)

1. Lockfile-delta extraction fixture-tested per ecosystem (npm, pnpm, NuGet).
2. An AGPL-introducing agent branch blocks the merge button until acknowledged.
3. Vault round-trip against a dev-mode container.
4. Gate runs offline (local SPDX data; no network at `Verified` time).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Vault_RoundTrip` (`RequiresDocker`) | dev-mode Vault: Set/Get/Delete; sealed → typed |
| 2 | `Aws_RoundTrip` (seamed/localstack) | same semantics |
| 3 | `LicenseGate_ClassificationMatrix` | fixture deltas: AGPL blocking, GPL blocking, MIT pass, dual-license reviewable, unknown default |
| 4 | `LockfileDelta_PerEcosystem` | npm/pnpm/NuGet fixtures → exact added/updated sets (shared with/extending P2-11 tests) |
| 5 | `Gate_BlocksCanMerge_EndToEnd` | branch adding AGPL dep → `Verified` + flagged item + `CanMerge=false` until ack |
| 6 | `BackendSwitch_Migration` | keys present in target, verified, removed from source |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second lockfile parser; network lookups at gate time; plaintext fallback on
backend failure; interface growth on `ISecureKeyStore`.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~LicenseGate|FullyQualifiedName~LockfileDelta|FullyQualifiedName~KeyStore"
grep -rn "HttpClient" GitLoom.Core/Compliance/    # 0 hits (offline gate)
```

---

## 8. Definition of done

- [ ] Vault KV2 + AWS backends behind `ISecureKeyStore`; selector + migration.
- [ ] Offline license gate (SPDX snapshot, expression handling, copyleft classes) at `Verified`, feeding the flagged panel as blocking.
- [ ] End-to-end AGPL block proven; per-ecosystem fixtures green.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-24**, base `phase2`.
