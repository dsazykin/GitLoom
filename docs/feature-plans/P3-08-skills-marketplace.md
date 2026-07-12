# P3-08 — Agent Skills Marketplace (Format-First) — Implementation Plan

**Task ID:** P3-08 · **Milestone:** M10+ (build the **format** early, the store later) ·
**Priority:** P2.
**Depends on:** P2-22 (adapter channel — the signed-manifest pipeline), P2-14 (governance).
**Branch:** implement on `feature/P3-08-skills-marketplace` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — manifest/signature corpus, egress-ack flow, install round-trip in a fixture sandbox with a host-side process watch; no human step.
>
> **Source of truth:** §P3-08 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **v1 is free packs only** — marketplace payments before the format is proven is a rejection
> trigger.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P3-08 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P3-08** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P3-08 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-22 ships a signed, pinned `adapters.json` channel with in-VM installation mechanics. Skills
(prompt/config bundles that specialize an agent) reuse that exact pipeline with a different
payload type. P2-11's acknowledgment panel pattern covers the security-relevant part: a pack
that wants extra egress domains needs explicit user acknowledgment.

### What you can rely on

| Fact | Where |
|---|---|
| Signed-manifest fetch/verify/install pipeline + pinning | P2-22 `AdapterChannel` |
| Sandbox exec for in-VM installs; egress allowlist model + change logging | P2-07 |
| Item-by-item acknowledgment panel pattern | P2-11 |
| Audit events | P2-15 |
| T-30 secret scanner (policy checks) | `PreCommitScanner` |
| Agent profiles / per-repo settings | existing settings + P2-31 dispatcher context |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Skills/SkillPack.cs` (manifest model + archive format) + `SkillPackSchema.json` |
| **Create** | `GitLoom.Core/Skills/SkillPackVerifier.cs` (signature + policy checks: egress denylist, secret scan, size caps) |
| **Create** | `GitLoom.Core/Skills/SkillInstaller.cs` (install/update/remove into an agent's sandbox via P2-22 mechanics; per repo or per agent profile) |
| **Create** | `GitLoom.Core/Skills/SkillRegistryClient.cs` (GitLoom-owned registry index; same signed pipeline as `adapters.json`) |
| **Create** | `GitLoom.App/ViewModels/Skills/SkillBrowserViewModel.cs` + views (search/install/update/remove; egress-ack flow) |
| **Create** | registry repo scaffolding: `registry/` layout + automated policy checks (CI: denylist, secrets, size) — documented for community PR submissions |
| **Create** | `GitLoom.Tests/SkillPackSchemaTests.cs`, `SkillVerifierTests.cs`, `SkillInstallRoundTripTests.cs`, `EgressAckFlowTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **`SkillPack`** — a **signed archive**; manifest: name, version, target CLIs, prompts/config
  shims, required egress domains, required tools. Installed into an agent's sandbox via the
  adapter-channel mechanics — **never executing host-side code**.
- Distribution v1 = a GitLoom-owned registry index (same signed-manifest pipeline as
  `adapters.json`); community submission = **PR to a registry repo** with automated policy
  checks (egress domains against a denylist, no secrets, size caps).
- In-app browser: search/install/update/remove per repo or per agent profile; installed packs
  recorded in **audit events**.

---

## 3. Implementation steps

1. **Format:** archive = zip with `skill.json` (schema-validated) + `prompts/` (markdown/system
   fragments) + `config/` (adapter config shims, same shim model as P2-22) + optional
   `tools.json` (declares required in-sandbox tools → installed via devbox, pinned versions).
   Signature: detached Ed25519 over the archive hash; registry publishes the signing pubkey via
   the same trust root as the adapter channel. **Packs are data + prompts, never host
   executables** — the schema has no executable-entry field, and the verifier rejects archives
   containing files outside the allowed trees (invariant 1).
2. **Verifier:** signature check → schema validation → policy checks: egress domains vs
   denylist, T-30 secret scan over all text, size caps (archive + per-file). Every failure
   typed and user-visible.
3. **Installer:** per repo or per agent profile — installation = unpack into the agent sandbox
   (`/opt/gitloom-skills/<id>@<ver>`), apply config shims at adapter launch, prepend prompt
   fragments into the spawn context (P2-34 pack integration: skill prompts ride as rules with
   their own digest). **Extra egress domains require explicit user acknowledgment** before the
   allowlist gains them (P2-11 ack-panel pattern; the allowlist change is logged per P2-07).
   Update = versioned swap; remove = unpack deletion + allowlist entries retired + config shims
   reverted. Install/update/remove → audit events.
4. **Registry:** `SkillRegistryClient` fetches the signed index (name→versions→hashes→urls);
   pinned-version installs (never `@latest` — same rule as adapters). The community registry
   repo carries CI policy checks mirroring the local verifier (defense in both places).
5. **Browser UI:** searchable list (index metadata), detail pane (prompts preview, required
   egress/tools shown **before** install), install/update/remove with per-repo/per-profile
   scoping; installed list with versions.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| unsigned / bad-signature pack | rejected before unpack |
| pack declaring a denylisted egress domain | verifier rejection (registry CI also catches) |
| pack with extra (allowed) egress domains | install blocked until item-by-item acknowledgment |
| secret-looking string in prompts | T-30 scan rejection |
| update with changed egress set | re-acknowledgment required for the delta |
| remove | sandbox files gone, allowlist entries retired, shims reverted |
| archive containing files outside allowed trees | rejected (no executable smuggling) |

---

## 5. Invariants (MUST)

1. Packs are data + prompts, never host executables.
2. Extra egress domains require explicit user acknowledgment (same panel pattern as P2-11).
3. Signature verified **before** install; pinned versions only.
4. Install/update/remove are audit events.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Schema_ValidInvalidCorpus` | manifest fixtures → exact validation outcomes |
| 2 | `Verifier_SignatureAndPolicy` | tampered archive, denylisted domain, seeded secret, oversize → typed rejections |
| 3 | `Install_RoundTripInFixtureSandbox` | install → files + shims + prompt injection present; update swaps; remove cleans (incl. allowlist retirement) |
| 4 | `EgressAck_Flow` | extra domain → blocked until acked; ack recorded; update-delta re-ack |
| 5 | `NoExecutablePath` | archive with a stray binary/script outside allowed trees → rejected |
| 6 | `Audit_LifecycleEvents` | install/update/remove chained |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** unsigned pack execution; payments in v1; host-side execution of pack content;
`@latest` installs; silent allowlist growth.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~SkillPack|FullyQualifiedName~SkillVerifier|FullyQualifiedName~SkillInstall|FullyQualifiedName~EgressAck"
grep -rn "@latest" GitLoom.Core/Skills/    # 0 hits
```

---

## 8. Definition of done

- [ ] Signed pack format + schema + verifier (policy checks incl. denylist/secrets/size/tree rules).
- [ ] Installer (repo/profile scoped, shims + prompt injection, ack-gated egress, audited lifecycle).
- [ ] Registry client on the adapter-channel trust pipeline + community-registry CI checks.
- [ ] Browser UI; all edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P3-08**, base `phase2`.
