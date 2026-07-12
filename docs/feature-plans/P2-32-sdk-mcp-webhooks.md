# P2-32 — External Automation Surface: SDK, MCP Server, Webhooks & Chat Notifications — Implementation Plan

**Task ID:** P2-32 · **Milestone:** M7.75 · **Priority:** P1-parity (Superset SDK/MCP/Slack;
the automation surface is the biggest structural gap across competitors).
**Depends on:** P2-02 (protos are the contract), P2-30 (automation semantics).
**Branch:** implement on `feature/P2-32-sdk-mcp-webhooks` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — generated-SDK contract tests against the in-proc daemon, MCP governed-task flow, webhook schema/retry; no human step.
>
> **Source of truth:** §P2-32 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat:** the MCP surface makes GitLoom itself agent-drivable **under the same governance** —
> plan approval and budgets apply to MCP-initiated work. A GitHub Action ships as a consumer of
> this surface (per §1.2/§3 notes).

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-32 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-32** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-32 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

The daemon's gRPC contract (P2-02, kept transport-agnostic by G-14/P2-25) is complete but
private. T-18 built an `ActionRegistry` for the command palette. This task publishes the
contract: generated TS + C# SDKs, an MCP endpoint reusing the action registry as the tool
surface, and outbound webhooks/chat notifications.

### What you can rely on

| Fact | Where |
|---|---|
| Proto-first contract; lint keeps it clean | `GitLoom.Protos/`, P2-25 |
| Auth interceptors + permission map (SDK/MCP get **no bypass**) | P2-02/P2-23 |
| `ActionRegistry` (T-18) — named, parameterized actions | `GitLoom.Core` command-palette surface |
| Governed pipeline: plans/approvals/budgets/queue | P2-14/P2-08/P2-10 |
| Audit log (every external call audited) | P2-15 |
| Event stream (queue transitions, escalations, budget events) | P2-10/P2-26/P2-08 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `sdk/typescript/` (generated grpc-web/connect client + hand-written thin wrapper; npm package skeleton) |
| **Create** | `sdk/dotnet/` (`GitLoom.Sdk` package: generated client + wrapper) |
| **Create** | `build/sdk-gen/` (codegen scripts pinned to proto versions; CI job) |
| **Create** | `GitLoom.Server/Mcp/McpEndpoint.cs` (MCP server: stdio/HTTP transport per spec version pinned in code) + `McpToolMapper.cs` (ActionRegistry → MCP tools) |
| **Create** | `GitLoom.Core/Notifications/Outbound/WebhookNotifier.cs`, `SlackTemplates.cs`, `TeamsTemplates.cs`, `EventRouting.cs` (per-event-type routing) |
| **Create** | `.github/actions/gitloom/` (GitHub Action consuming the SDK — dispatch a governed task, await outcome) |
| **Create** | `GitLoom.App/ViewModels/IntegrationsViewModel.cs` + view (webhook/chat config, MCP enable, SDK token management) |
| **Create** | `GitLoom.Tests/SdkContractTests.cs`, `McpToolMappingTests.cs`, `McpGovernanceTests.cs`, `WebhookSchemaRetryTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **SDK:** thin, versioned **TypeScript + C#** clients generated from the protos, covering:
  list/spawn agents, queue state, verification records, audit read.
- **MCP server:** the daemon exposes an MCP endpoint so external agents/IDEs can drive GitLoom
  ("spawn a worker on ticket X", "what's blocking the queue") — the T-18 `ActionRegistry` reused
  as the tool surface; **every MCP call authenticated + audited like any client**.
- **Webhooks + chat:** outbound notifications (queue transitions, escalations, budget events) to
  generic webhook + Slack/Teams templates; per-event-type routing.
- **Invariants:** SDK/MCP have **no privileged bypass** — same interceptors, same audit; webhook
  payloads carry links + metadata, **never diff/file content by default**.

---

## 3. Implementation steps

1. **SDK generation:** CI job generates clients from the pinned protos (TS: connect-es or
   grpc-web against the daemon's HTTP/2 endpoint; C#: `Grpc.Net.Client` + generated stubs).
   Hand-written wrapper layer: auth token loading, reconnect, typed errors, docs examples for the
   four covered areas. Version = proto package version; a **contract test** runs each SDK against
   the in-proc daemon (spawn is permission-gated → expect the governed pending-plan path, not a
   direct worker).
2. **Auth for external clients:** scoped API tokens minted in the UI (stored hashed daemon-side,
   full value shown once), carried as the same bearer metadata; identity = token name; P2-23
   permission map applies (a token without `spawn_agents` can read state only).
3. **MCP endpoint:** implement the MCP server protocol (pinned spec revision documented in
   code); tools generated from `ActionRegistry` entries whitelisted for external exposure
   (explicit `ExternallyExposed` flag on registry entries — not everything the palette can do is
   an external tool). Tool calls → the same service paths: "spawn a worker on ticket X" lands a
   **pending plan** (P2-14 approval unless policy auto-approve); "what's blocking the queue" →
   read RPCs. Every call audited (`mcp_call` event with tool + identity).
4. **Webhooks/chat:** `EventRouting`: per-event-type → sinks (generic webhook JSON, Slack blocks
   template, Teams adaptive card template). Payloads: event type, ids, links (`gitloom://` +
   host URLs), metadata — no diff/file content by default (opt-in field for content is out of
   scope). Delivery: bounded retry queue (reuse the P2-16 exporter's bounded-queue pattern; a
   shared small helper is acceptable — do not duplicate wholesale).
5. **GitHub Action:** `action.yml` + node script using the TS SDK: inputs (task prompt/ticket,
   repo, await-outcome flag) → dispatch → optionally poll to terminal state → set outputs
   (branch, verification result, links). Runs against a self-hosted runner that can reach the
   daemon (documented; LAN/tunnel is the user's transport choice).
6. **Docs:** `docs/sdk.md` quickstarts (TS/C#/MCP/Action) with the governance model stated up
   front.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| MCP tool call to spawn without approval policy | pending plan created; tool result says "awaiting human approval" — never a silent worker |
| SDK token lacking a permission | `PERMISSION_DENIED` typed through the SDK error surface |
| webhook sink down | bounded retry; loud state; other sinks unaffected |
| MCP call for a non-exposed action | tool not listed; direct call rejected |
| payload content probe | queue-transition payloads contain no diff/file content |

---

## 5. Invariants (MUST)

1. SDK/MCP have no privileged bypass — same interceptors, same audit (`mcp_call`/token identity).
2. Webhook payloads: links + metadata only by default.
3. MCP-initiated work obeys plan approval and budgets.
4. SDKs are generated from the protos — no hand-maintained parallel API surface.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `SdkContract_DotNet` / `SdkContract_TypeScript` (CI) | generated clients against in-proc daemon: list/queue/verification/audit reads green; spawn lands pending plan |
| 2 | `McpToolMapping` | only `ExternallyExposed` registry entries become tools; schema round-trip |
| 3 | `McpGovernance_SpawnGated` | MCP spawn → pending plan + `mcp_call` audit; budget checks applied |
| 4 | `Token_PermissionScoping` | scoped token → denied on unpermitted RPC; audit event |
| 5 | `Webhook_SchemaAndRetry` | payload schema-valid; sink outage → bounded retry + loud state |
| 6 | `Payload_NoContentByDefault` | transition payloads scanned: no patch/diff markers |
| 7 | Action smoke (CI, optional gate) | action dispatches against in-proc daemon fixture, outputs set |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a privileged internal channel for SDK/MCP; hand-written API duplicating protos;
diff content in default payloads; MCP tools auto-exposing the whole registry.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~SdkContract|FullyQualifiedName~Mcp|FullyQualifiedName~Webhook"
grep -rn "ExternallyExposed" GitLoom.Core/ | wc -l    # explicit flags exist; spot-check the list
```

---

## 8. Definition of done

- [ ] Generated TS + C# SDKs (pinned codegen, contract tests, npm/nuget skeletons) + scoped tokens.
- [ ] MCP endpoint over the flagged ActionRegistry surface, fully governed + audited.
- [ ] Webhooks/Slack/Teams routing with bounded retry; no content by default.
- [ ] GitHub Action consumer + `docs/sdk.md`.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-32**, base `phase2`.
