# P2-23 — Enterprise Access & Policy: RBAC / SSO / SCIM — Implementation Plan

**Task ID:** P2-23 · **Milestone:** M8 · **Priority:** P2 (enterprise GA)
**Depends on:** P2-15 (audit), P2-16 (SIEM), P2-22 (loopback OAuth infra).
**Branch:** implement on `feature/P2-23-rbac-sso-scim` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated in-proc (role x RPC matrix, SCIM harness, policy propagation) + one live-IdP smoke before enterprise GA.
> Enforcement is interceptor-level and provable with hand-crafted clients; OIDC group mapping uses fixture tokens. A single human-run smoke against a real IdP (Okta/Entra) belongs to the enterprise-GA checklist, not the PR gate.
>
> **Source of truth:** §P2-23 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §H-8.4). The core rule: **enforcement lives in daemon interceptors** — UI hiding is
> not enforcement.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-23 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-23** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-23 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

The daemon authenticates a single local user via the session token (P2-02); P2-14 added a
role-shaped interceptor for the coordinator. Enterprises need named humans with roles, IdP-driven
membership, and centrally signed policy. This task generalizes the auth layer: identity on every
gRPC call, role→permission enforcement in interceptors, OIDC SSO via the P2-22 loopback listener,
SCIM provisioning, and a signed policy document consumed by the gateway and egress configurator.

### What you can rely on

| Fact | Where |
|---|---|
| `BearerTokenInterceptor` + per-call metadata; `RoleInterceptor` (coordinator denial) | P2-02 / P2-14 |
| `LoopbackOAuthListener` + PKCE (reused for OIDC) | P2-22 |
| Audit events for permission-relevant actions | P2-15 |
| Gateway budgets + egress allowlist (policy consumers) | P2-08 / P2-07 |
| Daemon SQLite | P2-02 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Access/Permissions.cs` (`Permission` enum: `spawn_agents`, `approve_plans`, `approve_merges`, `edit_egress`, `edit_budgets`) + `Role.cs` (role → permission set; built-ins Admin/Operator/Reviewer/Viewer + custom) |
| **Create** | `GitLoom.Core/Access/IdentityResolver.cs` (call metadata → identity + roles) |
| **Edit** | `GitLoom.Server/Auth/RoleInterceptor.cs` → generalize to `PermissionInterceptor` (RPC → required permission map; every mutating RPC mapped) |
| **Create** | `GitLoom.Core/Access/OidcSsoService.cs` (OIDC code+PKCE via loopback; IdP group → role mapping) |
| **Create** | `GitLoom.Server/Scim/ScimEndpoint.cs` (SCIM 2.0 Users: create/get/patch/deactivate; bearer-token-guarded HTTP surface) |
| **Create** | `GitLoom.Core/Access/PolicyDocument.cs` + `PolicyVerifier.cs` (signed policy: model allowlists, egress rules, budgets; signature check; hot reload) |
| **Edit** | `IAiGateway` + `EgressProxyConfigurator` consume policy updates without restart |
| **Create** | `GitLoom.App/ViewModels/AccessSettingsViewModel.cs` + view (roles, mappings, policy status) |
| **Create** | `GitLoom.Tests/PermissionInterceptorTests.cs`, `RoleMappingTests.cs`, `ScimRoundTripTests.cs`, `PolicyVerifierTests.cs`, `PolicyHotReloadTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- Role → permission set over exactly the five permissions listed.
- **OIDC SSO** (loopback + PKCE) mapping IdP groups → roles.
- **SCIM 2.0** provisioning endpoint (create/deactivate round-trip against a test harness).
- **Enforcement in daemon interceptors** — identity on every gRPC call; a role without
  `approve_merges` gets `PERMISSION_DENIED` on the merge RPC **even from a hand-crafted client**.
- **Signed centralized policy doc** (model allowlists, egress rules, budgets) fetched and
  enforced by the Gateway + egress configurator; updates propagate **without daemon restart**.

---

## 3. Implementation steps

1. **Permission map:** static registry `RpcName → Permission?` covering every mutating RPC
   (spawn/stop → `spawn_agents`; plan approve/reject → `approve_plans`; merge/override →
   `approve_merges`; allowlist edits → `edit_egress`; budget sets → `edit_budgets`; read RPCs
   unmapped = any authenticated identity). A **new mutating RPC without a map entry fails a
   registry-completeness test** — that's how the map stays current.
2. **Identity:** extend the auth metadata: session token (local single-user → implicit Admin,
   preserving today's UX) or an SSO-issued daemon token bound to `(subject, roles, expiry)`;
   `IdentityResolver` yields identity for interceptor + audit (`Append(..., osIdentity)` gains
   the resolved subject).
3. **`PermissionInterceptor`:** resolves identity → checks the map → `PERMISSION_DENIED` with a
   typed reason; every denial emits an audit event. Coordinator role (P2-14) becomes a role with
   an empty permission set plus its tool-scoped allowances — one mechanism, not two.
4. **OIDC:** discovery doc → authorize (loopback+PKCE via P2-22) → tokens → validate ID token
   (issuer/audience/nonce/sig via JWKS) → group claims → role mapping table (configurable) →
   mint a daemon token. Refresh silently; clock-skew tolerant.
5. **SCIM 2.0:** minimal Users resource (POST create, GET, PATCH active=false deactivate) on a
   separate loopback-or-LAN HTTP listener guarded by a long-lived provisioning bearer token
   (keyring); deactivated user's daemon tokens revoked immediately (revocation list checked by
   the interceptor).
6. **Policy doc:** JSON `{modelAllowlist, egressRules, budgetCaps, version}` signed (Ed25519;
   org public key configured at enrollment); `PolicyVerifier` checks signature + version
   monotonicity; on accept → push into gateway (budget caps override local settings) + egress
   configurator (rules replace/extend the allowlist) via their existing update paths — no
   restart (test 5). Invalid signature → rejected loudly, previous policy stays.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| hand-crafted client, valid auth, missing permission | `PERMISSION_DENIED` on the mapped RPC |
| SCIM deactivate while user has a live session | next call rejected (revocation) |
| policy doc with bad signature | rejected; previous policy intact; loud state |
| policy version replay (older version) | rejected (monotonicity) |
| local single-user mode (no SSO configured) | implicit Admin; everything works as today |
| new mutating RPC added without permission mapping | registry-completeness test fails |

---

## 5. Invariants (MUST)

1. A role without `approve_merges` gets `PERMISSION_DENIED` on the merge RPC even from a
   hand-crafted client.
2. Policy updates propagate without daemon restart.
3. SCIM create/deactivate round-trips against the test harness.
4. Every denial and policy change is audited.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Interceptor_DeniesUnpermittedMerge` | raw gRPC client + Reviewer role → merge RPC `PERMISSION_DENIED`; audit event emitted |
| 2 | `PermissionRegistry_Complete` | reflection over service methods: every mutating RPC mapped |
| 3 | `OidcGroupMapping` | fixture ID tokens (groups matrix) → expected roles; invalid sig/nonce rejected |
| 4 | `Scim_CreateDeactivateRoundTrip` | harness: create → authenticate → deactivate → next call rejected |
| 5 | `Policy_HotReload` | new signed policy → gateway caps + egress rules updated live (no restart); bad sig/replay rejected |
| 6 | `LocalMode_ImplicitAdmin` | session-token identity passes all permissions |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** permission checks in ViewModels only; unmapped mutating RPCs; unsigned policy
acceptance; SCIM without token guard.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Permission|FullyQualifiedName~RoleMapping|FullyQualifiedName~Scim|FullyQualifiedName~Policy"
grep -rn "Permission" GitLoom.App/ViewModels/ | grep -v "IsVisible\|CanExecute"   # UI may hide, never enforce alone
```

---

## 8. Definition of done

- [ ] Five permissions, role model, complete RPC map, interceptor enforcement + audit on denial.
- [ ] OIDC SSO via loopback+PKCE with group→role mapping; token revocation.
- [ ] SCIM 2.0 users round-trip; signed policy with hot reload into gateway/egress.
- [ ] All edge rows tested. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-23**, base `phase2`.
