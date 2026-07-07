# P2-41 — Remote Dashboard: Daemon-Served LAN/Web Monitor — Implementation Plan

**Task ID:** P2-41 · **Milestone:** M8 · **Priority:** P1 (matches Orca/Superset/Nimbalyst
mobile, Pane Remote — the self-host model).
**Depends on:** P2-02, P2-32 (API), P2-13 (state model). Feeds P3-05/P3-06.
**Branch:** implement on `feature/P2-41-remote-dashboard` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-41 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **Model:** no vendor cloud — the daemon serves the SPA itself; a store-packaged mobile app is a
> later wrapper of the same API; cross-device continuity arrives with P3-06.

---

## 0. Context — what exists today

Everything the dashboard needs already streams over gRPC (agents, queue, approvals, spend, kill
switch) and P2-32 exposes it via grpc-web-compatible clients. Nothing serves a browsable surface.
This task adds a small responsive SPA hosted by the daemon (localhost + opt-in LAN with TLS),
with QR/short-code device pairing minting scoped tokens.

### What you can rely on

| Fact | Where |
|---|---|
| gRPC contract + grpc-web path (SDK codegen) | P2-02/P2-32 |
| Permission map + scoped tokens + audit identity | P2-23/P2-32 |
| Board projection (reuse — don't re-derive lanes) | P2-29 `BoardProjection` |
| Plan approvals (approve/reject RPCs, identity persisted) | P2-14 |
| Kill switch RPC | P2-14 |
| Spend counters | P2-08 |
| Needs-attention derivation | P2-13 attention helper |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `dashboard/` (SPA: TypeScript + the P2-32 TS SDK; small framework, static build) |
| **Create** | `GitLoom.Server/Web/DashboardHost.cs` (static hosting + grpc-web endpoint enablement; localhost default, LAN opt-in) |
| **Create** | `GitLoom.Server/Web/TlsProvisioner.cs` (self-signed cert generation; fingerprint exposed for pairing pinning) |
| **Create** | `GitLoom.Core/Access/DevicePairingService.cs` (QR/short-code → scoped token mint; roles approve/observe; revocation) |
| **Create** | `GitLoom.App/ViewModels/RemoteAccessViewModel.cs` + view (enable LAN, QR display, paired devices list, revoke) |
| **Edit** | protos (pairing RPCs) + CI job building the SPA into the daemon publish |
| **Create** | `GitLoom.Tests/DevicePairingTests.cs`, `RemoteRoleEnforcementTests.cs`, `RemoteApprovalIdentityTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

The daemon serves a small responsive SPA (localhost + optional LAN bind) over the gRPC-web API:
**session board** (P2-29 projection), **needs-attention list**, **plan approvals**
(approve/reject from the phone — the single highest-value remote action), **kill switch**,
**spend counters**.

**Device pairing:** QR/short-code pairing mints a **scoped token** (approve/observe roles); no
vendor cloud.

**Invariants:** LAN bind is opt-in + TLS (self-signed cert **pinned at pairing**); paired tokens
are scoped, revocable, audited; approvals from remote carry the paired identity into the P2-15
chain.

---

## 3. Implementation steps

1. **Hosting:** `DashboardHost` — Kestrel static files + grpc-web translation for the existing
   services (grpc-web middleware; same interceptors — auth/permissions untouched). Default bind
   `127.0.0.1:<port>`; LAN bind only when the user enables it in `RemoteAccessViewModel`
   (explicit toggle + firewall hint), and **only over TLS** with the `TlsProvisioner` cert.
2. **Pairing:** enable → daemon generates a short-lived pairing code (QR encodes
   `https://<lan-ip>:<port>/pair#<code>` + the **cert fingerprint**); the SPA pairing page posts
   the code → `DevicePairingService` mints a scoped token
   `(deviceName, role: approve|observe, created, revocable)` stored hashed; the browser stores it
   (localStorage) and pins the fingerprint (connection UI warns on mismatch). Codes single-use +
   2-minute expiry. Pair/revoke → audit events.
3. **Roles:** `observe` = read RPCs only; `approve` = + plan approve/reject + kill switch —
   mapped through the P2-23 permission map (new role presets; **enforcement stays in the daemon
   interceptors** — test with hand-crafted calls). Merge/spawn/budget mutations are **not**
   grantable to remote tokens in v1.
4. **SPA:** four screens — board (P2-29 lanes via the shared projection endpoint), attention
   list, approvals (plan render: scope files, approach, test strategy; approve/reject), status
   bar (spend counters, kill switch behind a hold-to-confirm). Responsive (phone-first),
   dark/light. Built with the P2-32 TS SDK; static assets embedded in the daemon publish (CI
   builds the SPA; no CDN — self-contained).
5. **Identity into audit:** approval RPCs from a paired token record
   `identity = device:<name>(<role>)` in the P2-14 approval row + P2-15 chain.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| pairing code reused / expired | rejected; single-use enforced |
| observe-role token calls approve RPC | `PERMISSION_DENIED` (interceptor, not UI) |
| token revoked while the SPA is open | next call rejected; SPA shows signed-out state |
| LAN disabled while devices paired | listener closed; tokens dormant (still valid on re-enable, still revocable) |
| cert regenerated | paired devices warn on fingerprint mismatch until re-paired |
| remote approval | plan row + audit carry the device identity |

---

## 5. Invariants (MUST)

1. LAN bind opt-in + TLS; cert pinned at pairing.
2. Paired tokens scoped, revocable, audited.
3. Remote approvals carry the paired identity into the audit chain.
4. Same interceptors — no dashboard-privileged path.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Pairing_MintScopedToken` | code → token with role; hashed at rest; audit event |
| 2 | `Pairing_SingleUseExpiry` | reuse + expired code rejected |
| 3 | `Role_ObserveCannotMutate` | hand-crafted approve/kill calls with observe token → denied |
| 4 | `Role_ApproveCanApprove_NotMerge` | approve token: plan RPCs ok; merge RPC denied |
| 5 | `Revocation_Immediate` | revoke → next call rejected |
| 6 | `RemoteApproval_IdentityInChain` | approval → P2-14 row + P2-15 event carry `device:` identity |
| 7 | SPA smoke (CI) | built SPA loads against in-proc daemon; board/approvals render fixture data (playwright-lite or dom assertion) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** plaintext LAN serving; unscoped/never-expiring pairing codes; dashboard-only API
shortcuts; remote merge/spawn grants in v1; CDN assets.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~DevicePairing|FullyQualifiedName~RemoteRole|FullyQualifiedName~RemoteApproval"
grep -rn "http://" dashboard/src/ | grep -v localhost   # no plaintext remote endpoints
```

---

## 8. Definition of done

- [ ] Daemon-hosted SPA (board/attention/approvals/kill-switch/spend), embedded static build.
- [ ] Opt-in LAN + TLS with pairing-time fingerprint pinning; QR/short-code pairing → scoped revocable audited tokens.
- [ ] Interceptor-enforced roles; remote approvals identity-chained.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-41**, base `phase2`.
