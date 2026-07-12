# P2-17 — Source-Available Trust Architecture + Network Transparency — Implementation Plan

**Task ID:** P2-17 · **Milestone:** M7.5 · **Priority:** P0 for enterprise GA (licensing already
LOCKED: FSL backend / proprietary GUI + Coordinator).
**Depends on:** P2-07 (egress proxy logs).
**Branch:** implement on `feature/P2-17-source-available-transparency` off `phase2`; PR targets
`phase2`.

> **Verification profile:** Automated (CI license check + VM streaming tests) + **human doc-claims review required**.
> The transparency view and license boundary are automated. But every claim in `docs/security-architecture.md` must map to a named test or config reference — that checklist is a human review artifact pasted into the PR, and the FSL/proprietary split is a judgment call a human confirms.
>
> **Source of truth:** §P2-17 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §H-8.1). The licensing decision is **not** open for redesign in this task — implement
> the locked boundary.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-17 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-17** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-17 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

Everything lives in one private repo. The trust thesis ("you can read the code that runs your
agents and watch every byte it sends") requires: a repo split along the locked license boundary,
a published security architecture document, and an in-app live view of all outbound network
activity sourced from the P2-07 proxy logs.

### What you can rely on

| Fact | Where |
|---|---|
| Egress proxy with allowlist + per-connection logging (`AllowlistChanged` events; verdict logs) | P2-07 `EgressProxyConfigurator` |
| Daemon/UI split along gRPC (G-18) — the split boundary already runs through process lines | P2-02 |
| CI pipeline (GitHub Actions) | `.github/workflows/ci.yml` |
| Docs conventions | `docs/` |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | repo-split tooling: `build/split/fsl-manifest.txt` (paths that publish to the FSL repo: `GitLoom.Server`, `GitLoom.Protos`, `GitLoom.Core/Agents/**` (daemon+sandbox+worktree engine), adapters, `images/**`) + sync workflow (subtree/filter publish job) |
| **Create** | `LICENSE.FSL` in published tree; `LICENSE` headers per artifact; NuGet packaging (`GitLoom.Daemon.*` packages) the private repo pins by version |
| **Create** | `docs/security-architecture.md` (lives in the FSL repo beside the code; mirrored here until the split lands) |
| **Create** | `SECURITY.md` (vulnerability intake for the commissioned pre-GA audit) |
| **Create** | `GitLoom.Core/Agents/Sandbox/EgressLogStream.cs` (daemon: proxy log tail → typed `ConnectionEvent` stream over gRPC) + proto |
| **Create** | `GitLoom.App/ViewModels/NetworkTransparencyViewModel.cs` + `Views/NetworkTransparencyView.axaml(.cs)` (live panel: destination, agent, bytes, verdict; filterable; exportable CSV/JSON) |
| **Create** | CI license-boundary check (`build/split/check-license-boundary.sh`) |
| **Create** | `GitLoom.Tests/EgressLogStreamTests.cs`, `NetworkTransparencyViewModelTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

1. **Repo split enforcing the license boundary:** daemon + sandbox/worktree engine + adapters →
   FSL repo publishing NuGet artifacts; GUI/Coordinator/governance stay private and **pin**
   versions of those artifacts.
2. **Published `docs/security-architecture.md`** living in the FSL repo next to the code it
   describes — every claim maps to a test or config reference.
3. **Network transparency view:** in-app panel streaming every outbound connection from daemon +
   sandboxes (source = egress proxy logs): destination, agent, bytes, verdict; filterable;
   exportable.
4. Independent security audit commissioned pre-enterprise-GA with a `SECURITY.md` intake.

---

## 3. Implementation steps

1. **Boundary manifest + CI check:** `fsl-manifest.txt` lists published paths.
   `check-license-boundary.sh` fails CI when (a) a manifest path references a private-only
   assembly (GUI, Coordinator, governance), or (b) a published file lacks the FSL header /
   private file carries one. Header lint by glob.
2. **Publish pipeline:** a workflow job builds the manifest subtree into the public FSL repo
   (history-filtered publish; `git filter-repo` or subtree push — pick one and document it in the
   workflow) and pushes NuGet packages (`GitLoom.Daemon`, `GitLoom.Protos`, versioned with the
   app release train). The private repo consumes those packages by **pinned version** in the
   affected csproj files — after the split, `GitLoom.App` must not project-reference published
   code (enforced by the boundary check; a transition period with both is acceptable inside this
   task only).
3. **`docs/security-architecture.md`:** sections — threat model (prompt injection → exfil;
   hostile package scripts; token theft), the G-11/G-15/G-16 container spec with test references,
   egress design (proxy, DNS pinning, iptables backstop → P2-07 test names), credential flow
   (P2-01 keyring → tmpfs), quarantine remotes (P2-06 test), audit chain (P2-15). **Every claim
   carries a `(test: …)` or `(config: …)` reference** — the doc-claims checklist in the PR maps
   them.
4. **`EgressLogStream`:** the P2-07 proxy writes structured connection logs (extend its config if
   it currently logs free-text): `{ts, agentId, destHost, destPort, bytesOut, bytesIn, verdict}`.
   Daemon tails them into a bounded ring + gRPC server-stream (`NetworkService.StreamConnections`
   + `authorization` like every RPC).
5. **Transparency panel:** live grid (virtualized), filters (agent, verdict, host substring),
   byte counters, allowlist-change events interleaved (from P2-07's change log), export current
   view to CSV/JSON. The acceptance demo: an allowed call and a denied attempt both visible
   **within seconds** of occurring.
6. **`SECURITY.md`:** disclosure policy, contact, scope, safe-harbor language; link from README.

---

## 4. Edge-case matrix (binding — each row needs a test or CI check)

| Case | Required behavior |
|---|---|
| private-only type referenced from a published path | CI boundary check fails |
| missing/incorrect license header | CI fails, names the file |
| proxy log rotation mid-stream | tail survives rotation; no dropped/duplicated events in the window test |
| denied egress attempt | appears in the panel with verdict `Denied` within seconds |
| export with active filters | exported rows == filtered view |

---

## 5. Invariants (MUST)

1. License headers / `LICENSE` correct per artifact (CI check).
2. The transparency view shows a live allowed call and a denied attempt within seconds.
3. Every security-architecture doc claim maps to a test or config reference (checklist in the PR).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | CI `check-license-boundary` | seeded violations (fixture branch) → fails with the exact file list |
| 2 | `EgressLogStream_ParsesAndStreams` | fixture log lines → typed events in order; rotation handled |
| 3 | `TransparencyVm_FilterAndExport` | filters narrow the collection; export matches view |
| 4 | `TransparencyVm_LiveLatency` (integration, `RequiresDocker`) | proxied allowed + denied requests → both events surfaced < 5 s |
| 5 | Doc-claims checklist | PR table: claim → test/config reference (reviewer verifies spot checks) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** publishing GUI/Coordinator/governance code; weakening the boundary check to
warnings; a transparency panel fed by anything other than the proxy logs (self-reporting is not
transparency); claims in the security doc without references.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~EgressLogStream|FullyQualifiedName~NetworkTransparency"
bash build/split/check-license-boundary.sh          # clean
grep -c "(test:\|(config:" docs/security-architecture.md   # ≥ claim count — spot-check the map
```

---

## 8. Definition of done

- [ ] Boundary manifest + CI enforcement + publish pipeline + NuGet pinning path.
- [ ] `docs/security-architecture.md` with claim→evidence mapping; `SECURITY.md` intake.
- [ ] Live network transparency panel (filter/export) fed by proxy logs; latency demo green.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-17**, base `phase2`.
