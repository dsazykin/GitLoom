# P3-07 — Host-Provider Parity: GitLab (full), Bitbucket, Azure DevOps — Implementation Plan

**Task ID:** P3-07 (a/b/c) · **Milestone:** M10 — but **independent; may start any time** ·
**Priority:** P1 — removes "GitHub-only" from every review of the client.
**Depends on:** T-23…T-28 seams (all shipped; non-GitHub hosts are typed stubs today).
**Branch:** `feature/P3-07-host-parity` holds this plan; **each host lands as its own PR from
its own sub-branch** (`feature/P3-07a-gitlab`, `feature/P3-07b-bitbucket`,
`feature/P3-07c-azure-devops`) — one host = one task.

> **Verification profile:** Fully automated per-host fixture suites + one nightly live smoke per host (`RequiresNetwork`); no human step in the PR gate.
>
> **Source of truth:** §P3-07 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Transport rule:** one transport class per host, mirroring `GitHubApiClient` — a second
> *GitHub* transport remains a rejection trigger.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P3-07 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P3-07** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P3-07 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

T-23…T-28 built provider seams (`IPullRequestProvider`, `IIssueProvider`, `ICheckProvider`,
`INotificationProvider`, `IReleaseProvider`, `ICommitContextProvider`) with GitHub as the only
real implementation; `HostProviderRegistry` dispatches by detected host; non-GitHub hosts return
typed "unsupported". `GitHostDetector` already recognizes host kinds; T-14 handles multi-host
auth storage (`token_<host>`); the GitLab OAuth app id is a placeholder (Backlog B-2).

### What you can rely on

| Fact | Where |
|---|---|
| Provider interfaces + registry dispatch + `IsSupported` matrix | `GitLoom.Core/Hosting/`, T-23…T-28 services |
| The audited-transport discipline (header-only token, `Redact`, typed errors incl. rate limits, `HttpMessageHandler` seam, recorded fixtures) | `GitHubApiClient` |
| Auth flows: device flow, PAT dialog (already built), loopback OAuth (P2-22) | T-14/P2-22, `AccountsViewModel` |
| P2-12 external PR intake consumes `IPullRequestProvider` host-agnostically | P2-12 |

---

## 1. Files to create (per host, its own PR)

| Host | Files |
|---|---|
| **a GitLab** | `GitLoom.Core/Hosting/GitLab/GitLabApiClient.cs` + `GitLabPullRequestProvider.cs` (MRs), `GitLabIssueProvider.cs`, `GitLabCheckProvider.cs` (pipelines), `GitLabNotificationProvider.cs` (todos), `GitLabReleaseProvider.cs`, `GitLabCommitContextProvider.cs`; OAuth app id registration (replaces the placeholder); fixtures + `GitLoom.Tests/GitLabProviderTests.cs` |
| **b Bitbucket Cloud** | `Bitbucket/BitbucketApiClient.cs` + PR/issue (or Jira link-out)/pipelines providers; notifications stays typed-unsupported; fixtures + tests |
| **c Azure DevOps** | `AzureDevOps/AzureDevOpsApiClient.cs` + PR/work-item/pipeline-run providers; PAT-only auth (no device flow); fixtures + tests |

Each PR also edits: `HostProviderRegistry` registration, `IsSupported` matrix,
`GitHostDetector` coverage if gaps exist, `AGENTS.md` Repository Map.

---

## 2. Contract (binding)

For each host: implement the six provider interfaces + auth completion, with the **same
fixture-driven offline test pattern as T-23** (recorded JSON, token-in-header-only audit, typed
error mapping incl. rate limits).

1. **P3-07a GitLab:** REST v4 — MRs, issues, pipelines, todos, releases; **register a real OAuth
   app id**; MR-specific semantics: approvals + merge-trains awareness as a **read-only**
   surface.
2. **P3-07b Bitbucket Cloud:** PRs, issues (or Jira link-out where the workspace uses Jira),
   pipelines; **no notifications API → typed "unsupported" stays for that panel only**.
3. **P3-07c Azure DevOps:** PRs, work items, pipeline runs; **no device flow → PAT dialog**
   (already built).

---

## 3. Implementation notes (apply to every host)

1. **Transport:** one `XxxApiClient` per host — constructor takes the `HttpMessageHandler`
   seam; token from `token_<host>` via `CredentialResolver`; auth header per host convention
   (GitLab `PRIVATE-TOKEN`/Bearer, Bitbucket Bearer, ADO PAT basic); every error path through
   the shared `RedactionExtensions`; rate-limit headers → the typed rate-limit exception the
   GitHub client already throws (same type — callers must not care which host limited them).
2. **Semantics mapping:** keep the provider models host-neutral (they already are — T-23 models)
   and translate per host: MR ⇆ PR, pipeline ⇆ check run (status mapping table per host,
   fixture-tested), todo ⇆ notification, work item ⇆ issue. Unmappable concepts → typed
   `NotSupportedByHost` on that operation only, never a crash.
3. **Auth:** GitLab OAuth (loopback where self-managed allows; PAT fallback); Bitbucket OAuth or
   app password; ADO PAT dialog. All storage in `SecureKeyring` (`token_<host>`), `Accounts`
   settings gains the host rows.
4. **P2-12 compatibility:** after each host lands, the external-PR-intake fixture suite runs
   against that host's provider (the intake is host-agnostic by design — prove it).
5. **Live smoke:** one network-gated trait test per host (real account) for list+detail; not in
   the default suite.

---

## 4. Edge-case matrix (binding, per host)

| Case | Required behavior |
|---|---|
| rate limit | typed rate-limit exception (shared type), backoff honored by callers |
| token invalid/expired | typed auth failure, message redacted |
| unsupported panel (e.g. Bitbucket notifications) | typed "unsupported" for that panel only; others work |
| GitLab merge-train-protected MR | read-only awareness surfaced; merge attempt gives the host's typed refusal |
| Bitbucket workspace on Jira issues | issue panel link-out mode, no error |
| ADO PAT with partial scopes | per-operation typed permission failures, not global failure |

---

## 5. Invariants (MUST)

1. One transport class per host; `HostProviderRegistry` stays the single dispatch point.
2. Fixture-driven offline tests for everything; live smokes network-gated.
3. Tokens header-only, keyring-stored, never logged (per-host grep test).
4. P2-12 intake works against each host's PR/MR list **unchanged**.

---

## 6. Test contract (per host PR)

| # | Test | Assertion |
|---|---|---|
| 1 | `List/Detail/Create/Merge/Close_Fixtures` | recorded JSON → host-neutral models; field mapping exact |
| 2 | `ErrorMapping_Matrix` | 401/403/404/429/5xx → typed exceptions; messages redacted |
| 3 | `TokenNeverLeaks` | transport audit: header-only; no token in URLs/logs/exceptions |
| 4 | `StatusMapping_Table` | pipeline/check-state fixtures → `CheckStateMapper`-compatible states |
| 5 | `IsSupported_MatrixUpdated` | registry reports the exact per-panel support set |
| 6 | `ExternalPrIntake_AgainstHost` | P2-12 fixture poll works with this provider |
| 7 | Live smoke (network-gated) | list PRs/MRs + one detail on a real account |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second GitHub transport; provider SDK dependencies; host semantics leaking into
the neutral models; panels crashing instead of typed-unsupported.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~GitLabProvider"     # (per host)
grep -rn "PRIVATE-TOKEN\|Bearer" GitLoom.Core/Hosting/GitLab/ | grep -v ApiClient   # transport-only
grep -rn "Octokit\|GitLabApiClient nuget" GitLoom.Core/       # 0 hits — thin REST
```

---

## 8. Definition of done (per host; the task closes when all three landed)

- [ ] Six providers + transport + auth completion; registry/`IsSupported` updated.
- [ ] Fixture suites (list/create/merge/close, errors, token-leak, status mapping) green offline.
- [ ] P2-12 intake proven against the host; live smoke behind the network trait.
- [ ] `AGENTS.md` Repository Map updated. **One host = one PR** linking **P3-07a/b/c**, base `phase2`.
