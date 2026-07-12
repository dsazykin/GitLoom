# P3-06 — Cloud Worktrees Implementation — Implementation Plan

**Task ID:** P3-06 · **Milestone:** M10 (private beta ≤ 2 quarters post-desktop-GA) ·
**Priority:** P0 of the wave (the scale + usage-revenue story).
**Depends on:** P2-25 guardrails green, P2-02…P2-10 (the whole platform).
**Branch:** implement on `feature/P3-06-cloud-worktrees-impl` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated WAN suite against a real pod (the acceptance) + tenant-isolation/metering/crypto-shred tests + **human ops validation**.
> The unchanged P2-14 suite at 80 ms RTT is the binding acceptance and is CI. Account-deletion crypto-shred and eviction behavior are automated against staging; a human validates the operational runbook (eviction, restore, export handover) before beta.
>
> **Source of truth:** §P3-06 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (P2-25
> step 2 becomes real). **The acceptance test already exists:** the unchanged P2-14 end-to-end
> suite at 80 ms RTT in CI — a cloud pod must pass it as-is.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P3-06 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P3-06** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P3-06 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

P2-25 kept every proto transport-agnostic and proved the suite over WAN latency; the daemon is
one binary. This task packages it as a per-tenant pod, swaps auth (mTLS + OIDC for the local
session token), adds cloud repo sync, per-tenant encryption, and metering.

### What you can rely on

| Fact | Where |
|---|---|
| Daemon binary runs linux-x64 headless; `--local-dev` proves config-driven bind | P2-02 |
| WAN CI job + endpoint seam in `DaemonClient` | P2-25 |
| Session leader / reattach patterns (pod restarts) | P2-09/P2-18 |
| `ISecureKeyStore` cloud backends pattern (KMS fits it) | P2-24 |
| Authenticated CLI push path; provisioner remote registration | P2-06/T-14 |
| `GatewayService` spend events (metering rides them) | P2-08 |
| OIDC identity (P2-23); pairing/scoped tokens precedent | P2-23/P2-41 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `deploy/pod/` (OCI image build for the daemon + sandbox engine; compose/helm chart for the front door) |
| **Create** | `GitLoom.Server/Cloud/MtlsFrontDoor.cs` (mTLS termination + OIDC token validation → daemon identity) |
| **Create** | `GitLoom.App/Services/CloudCredentialProvider.cs` (behind the existing `DaemonClient` seam — replaces the session-token read for cloud endpoints) |
| **Create** | `GitLoom.Core/Agents/CloudRepoSync.cs` (Windows-side: `gitloom-cloud` remote (HTTPS URL) registration + push path) |
| **Create** | `GitLoom.Server/Cloud/TenantStore.cs` (per-tenant encryption at rest: repo store + audit DB; tenant-scoped keys via KMS behind `ISecureKeyStore`) |
| **Create** | `GitLoom.Server/Cloud/Metering.cs` (compute-seconds + storage per session → spend events → billing export) |
| **Create** | `GitLoom.App/ViewModels/RemoteEnvironmentViewModel.cs` (`Local VM | GitLoom Cloud` picker, per-repo) |
| **Create** | `docs/adr/pod-topology.md` (2-week spike output: nested containers vs one-pod-per-agent) |
| **Create** | `GitLoom.Tests/Cloud/…` (isolation, metering, crypto-shred, reattach) |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **Pod image:** the daemon binary + sandbox engine as an OCI image (**same binary** — G-14);
  per-tenant pod, per-session worktree containers inside it (nested per the P2-07 spec, **or**
  flat with one pod per agent — decided by a 2-week spike, documented ADR).
- **Auth:** mTLS between client and pod front door + user OIDC token (P2-23); the local
  session-token mechanism replaced by a `CloudCredentialProvider` behind the existing
  `DaemonClient` seam.
- **Repo sync:** `git push gitloom-cloud` over HTTPS with the existing authenticated CLI path;
  the provisioner's Windows-side remote registration gains a cloud variant (URL instead of UNC).
- **Tenancy:** per-tenant encryption at rest (repo store + audit DB), tenant-scoped keys in a
  cloud KMS behind `ISecureKeyStore`.
- **Metering:** compute-seconds + storage per session streamed as `GatewayService` spend events
  → billing export.
- **`RemoteEnvironment` picker:** `Local VM | GitLoom Cloud`, per-repo.

---

## 3. Implementation steps

1. **Topology spike (first, time-boxed 2 weeks):** nested containers inside a tenant pod
   (userns/rootless nesting viability with the P2-07 hardening) vs flat one-pod-per-agent
   (simpler isolation, heavier orchestration). Deliverable: ADR + the chosen path's hardening
   test parity with P2-07 (`docker inspect`-equivalent assertions must all hold).
2. **Pod image:** multi-stage build → daemon publish + sandbox engine deps + pinned libvterm;
   config via env/mounted secrets (no baked credentials); health endpoint. Egress rules apply
   **in-cloud too** (invariant 2) — the P2-07 proxy/allowlist ships inside the pod.
3. **Auth swap:** `MtlsFrontDoor` — client cert per registered device (enrollment via OIDC
   login → cert issuance) + OIDC bearer per call; maps to the P2-23 identity/permission layer
   unchanged. `CloudCredentialProvider` implements the existing `DaemonClient` credential seam;
   the picker decides which provider a repo uses (invariant 3: per-repo choice, never a silent
   default).
4. **Repo sync:** pod-side git HTTPS endpoint (smart HTTP over the front door, tenant-scoped)
   → `CloudRepoSync` registers `gitloom-cloud` remote; pushes ride the **existing authenticated
   CLI path**. Provisioner in the pod treats the pushed bare repo exactly like the local ext4
   mirror.
5. **Tenancy:** per-tenant data root; encryption at rest with tenant keys from KMS (an
   `ISecureKeyStore` implementation per P2-24's pattern); audit DB per tenant. **Account
   deletion:** reap pods → export handover (repo bundle + audit export) → **crypto-shred** (KMS
   key deletion) — verification test proves ciphertext unrecoverable (edge row 3).
6. **Metering:** per-session compute-seconds (cgroup accounting) + storage sampling → spend
   events on the existing `GatewayService` stream (new event kind) → billing export file/API.
   Accuracy test against a scripted session (edge/required test).
7. **Resilience:** network drop → P2-18 snapshot reattach (edge row 1); pod eviction/restart →
   session-leader pattern reused; reattach or clean `Dead` state, never silent loss (edge
   row 2). Audit ordering by **pod sequence**, not client time (edge row 4).
8. **Acceptance:** run the unchanged P2-14 suite (via the P2-25 WAN harness pointed at a real
   pod) — **the** gate for this task.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| network drop mid-session | terminal reattach via the P2-18 snapshot path; queue state intact |
| pod eviction/restart | leader pattern reattach or clean `Dead` state, never silent loss |
| tenant deletes account | pods reaped, repo store + audit export handed over, then crypto-shredded |
| clock skew client↔pod | audit ordering by pod sequence, not client time |
| two tenants on shared infrastructure | zero cross-read (isolation test) |

---

## 5. Invariants (MUST)

1. The **unchanged** P2-14 end-to-end suite passes against a cloud pod.
2. No repo bytes leave the tenant boundary except via the user's own `git push`/provider API
   calls — egress rules apply in-cloud too.
3. Desktop→cloud is a per-repo choice, never a silent default.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | WAN suite vs real pod | unchanged P2-14 suite green (the acceptance) |
| 2 | `MultiTenant_Isolation` | two tenants: A's credentials cannot read B's repos/audit/streams |
| 3 | `Metering_Accuracy` | scripted session with known compute/storage → events within tolerance |
| 4 | `CryptoShred_Verification` | post-deletion: stored ciphertext + deleted key → unrecoverable (decrypt attempt fails) |
| 5 | `Reattach_NetworkDropAndEviction` | drop + restart scenarios → grid/queue continuity or clean `Dead` |
| 6 | `Egress_InCloudEnforced` | pod agent `curl https://example.com` → denied (P2-07 matrix re-run in-pod) |
| 7 | `Picker_PerRepoNoDefault` | new repo defaults local; cloud requires explicit selection |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a cloud-only fork of the daemon; tenant data in shared buckets without per-tenant
keys; egress relaxed in-cloud; auth bypassing the P2-23 layer.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Cloud"    # + the WAN suite job against a pod
grep -rn "#if CLOUD" GitLoom.Server/ GitLoom.Core/  # 0 hits — one binary
```

---

## 8. Definition of done

- [ ] Topology ADR + hardened pod image (same binary, in-pod egress enforcement).
- [ ] mTLS+OIDC front door with `CloudCredentialProvider` behind the existing seam; per-repo picker.
- [ ] HTTPS repo sync; per-tenant KMS encryption; deletion = export + crypto-shred (proven).
- [ ] Metering → spend events → billing export (accuracy-tested).
- [ ] Unchanged P2-14 suite green against a pod; all edge rows covered.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P3-06**, base `phase2`.
