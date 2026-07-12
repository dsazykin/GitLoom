# P2-27 — Ticket-to-Verified-PR Pipeline — Implementation Plan

**Task ID:** P2-27 · **Milestone:** M7.75 · **Priority:** P0 — the direct MergeLoom response.
**Depends on:** P2-10, P2-14; reuses T-24 (issues); P3-07 hosts slot in as they land.
**Branch:** implement on `feature/P2-27-ticket-to-pr-pipeline` off `phase2`; PR targets `phase2`.

> **Verification profile:** Automated fixture pipeline + scripted end-to-end + **model testing on plan-draft quality + human eval**.
> Provider mapping, routing rules, clarity check, write-back, and the full scripted loop are CI. `DraftPlanAsync` quality (does a real ticket produce a sane Scope/Approach/TestStrategy?) requires runs against a real model on a sample ticket set with a human grading the drafts — CI asserts shape, not judgment.
>
> **Source of truth:** §P2-27 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** ticket-to-**merged** (queue + radar + sandbox + approval + audit +
> cockpit), not MergeLoom's ticket-to-PR; vs Kepler — we carry the ticket through verification →
> merge → outcome write-back.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-27 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-27** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-27 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

T-24 ships GitHub Issues through the audited transport; P2-14 has the two-phase plan approval;
P2-10 the verified queue. Nothing connects "a ticket" to "an approved plan running in a sandbox"
and back. This task builds the intake (six providers), the clarity check, plan drafting, ticket
provenance through the pipeline, and outcome write-back.

### What you can rely on

| Fact | Where |
|---|---|
| `IIssueService` + audited GitHub transport (T-24) | `GitLoom.Core/Services/IssueService.cs`, `GitLoom.Core/Hosting/` |
| `TaskPlan` + `PlanApprovalService` (approval is never skipped) | P2-14 |
| Queue + `VerificationRecord` + merge paths (local + host-API via P2-12's `MergeDispatch`) | P2-10/P2-12 |
| Provenance trailers + Agent Trace emitter | P2-11 |
| Keyring conventions `token_<host>` → new `ticket_<provider>` | P2-01/`SecureKeyring` |
| Admission control for batch spawns | P2-08 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Orchestrator/TicketIntake.cs` (`ITicketIntake`, `TicketRef`, `TicketOutcome`) |
| **Create** | `GitLoom.Core/Tickets/ITicketProvider.cs` + providers: `GitHubIssueTicketProvider` (wraps T-24), `JiraTicketProvider.cs`, `LinearTicketProvider.cs`, `AzureBoardsTicketProvider.cs`, `MondayTicketProvider.cs` (one audited REST transport each; GitLab arrives with P3-07a) |
| **Create** | `GitLoom.Core/Tickets/TicketClarityCheck.cs` (pure grading of scope/AC) |
| **Create** | `GitLoom.Core/Tickets/TicketRoutingRules.cs` (per-repo label/status/query filters) |
| **Create** | `GitLoom.Core/Tickets/EpicImporter.cs` (epic/milestone/project → multi-task plan skeleton for P2-28; sync re-diff) |
| **Create** | `GitLoom.App/ViewModels/Tickets/TicketIntakeViewModel.cs`, `DraftPlanPreviewViewModel.cs` + views ("Start from ticket" flow) |
| **Create** | `GitLoom.Tests/TicketClarityCheckTests.cs`, `TicketProviderFixtureTests.cs`, `TicketRoutingTests.cs`, `OutcomeWriteBackTests.cs`, `GitLoom.Tests/Integration/TicketEndToEndTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// daemon GitLoom.Core/Agents/Orchestrator/TicketIntake.cs
public sealed record TicketRef(string Provider, string ExternalId, string Title, string Url); // github-issue | gitlab-issue | jira | linear
public interface ITicketIntake
{
    /// <summary>Drafts a TaskPlan (P2-14) from a ticket: title/body/labels/linked code refs →
    /// structured Scope/Approach/TestStrategy. The plan ALWAYS goes to human approval.</summary>
    Task<TaskPlan> DraftPlanAsync(string repoHash, TicketRef ticket, CancellationToken ct);
    /// <summary>Post-merge: writes the outcome back to the ticket (comment + optional transition)
    /// with links to the PR, the verification record, and the audit entry.</summary>
    Task ReportOutcomeAsync(TicketRef ticket, TicketOutcome outcome, CancellationToken ct);
}
```

Providers: GitHub Issues (T-24 transport), GitLab (P3-07a), **Jira, Linear, Azure Boards,
monday.dev** — thin REST clients, **one audited transport each**, keyring keys
`ticket_<provider>`.

---

## 3. Implementation steps

1. **Providers:** `ITicketProvider { SearchAsync(query/filters), GetAsync(id), CommentAsync,
   TransitionAsync(status) }`. Each new client mirrors `GitHubApiClient` discipline: token
   header-only, `Redact` on errors, typed failures, `HttpMessageHandler` seam, recorded fixtures.
   No provider SDK dependencies — thin REST.
2. **Routing rules:** per-repo config: provider + filters (label/status/JQL-or-query) defining
   what is *offered* for intake ("approved work only" — e.g. `status=Ready AND label=agent`).
   Pure predicate evaluation, settings UI list editor.
3. **`TicketClarityCheck` (pure):** grades title/body/labels → findings: missing acceptance
   criteria, vague scope verbs, conflicting labels, no linked code refs. Output renders on the
   draft screen as gaps + suggested questions to post back — **reviewer assistance, never a
   silent rejection** (MergeLoom gate-1 counter-design).
4. **`DraftPlanAsync`:** compose a drafting prompt (ticket fields + linked refs + context-vault
   pack when P2-34 is present) through the P2-08 gateway → structured `TaskPlan` JSON (P2-14
   schema-validated; invalid → one bounded retry then typed failure). The draft is **editable**
   in `DraftPlanPreviewViewModel`; approval is the standard P2-14 gate — never skipped for
   ticket-initiated work (invariant 1).
5. **Provenance through the pipeline:** the worker spawn carries `Task: <provider>:<externalId>`
   trailer + Agent Trace reference; `VerificationRecord` and the merge/PR link back to the ticket
   (store `TicketRef` on the queue entry).
6. **`ReportOutcomeAsync`:** comment template — PR/merge link, pass/fail + verification record
   reference, curated-commit summary (P2-20 output when used), audit entry id; optional status
   transition per provider config (**off by default**). Write-back without permission → typed,
   non-fatal (edge row 2).
7. **Batch intake:** multi-select → one plan per ticket → approvals individually; spawns
   sequential or parallel per admission control.
8. **Epic import & sync:** `EpicImporter` maps a Jira Epic / GitHub milestone / Linear project →
   a multi-task plan skeleton whose children become P2-28 task nodes; "Sync" re-diffs against the
   tracker (added/removed/changed children flagged for re-approval).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| ticket edited after plan drafted | plan shows a staleness chip (ticket updated-at vs draft time) |
| write-back without permission | typed, non-fatal; outcome saved locally, surfaced to user |
| two workers from the same ticket | allowed but labeled (comparison flow, P2-31) |
| clarity check finds gaps | draft screen shows gaps + suggested questions; intake still possible |
| provider rate limit | typed backoff path; no crash loop |
| plan draft fails schema twice | typed failure; nothing pending |

---

## 5. Invariants (MUST)

1. Plan approval is **never** skipped for ticket-initiated work.
2. Ticket tokens follow G-4/G-13: header-only, keyring (`ticket_<provider>`), never argv/logs.
3. One audited transport per provider.
4. Clarity check is pure and advisory.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Provider_FixtureMatrix` (per provider) | search/get/comment/transition against recorded fixtures; auth header-only; errors redacted |
| 2 | `ClarityCheck_GradingCorpus` | fixture tickets → expected gap sets + suggested questions |
| 3 | `Routing_FilterPredicates` | label/status/query fixtures → offered set |
| 4 | `DraftPlan_SchemaAndEdit` | drafted plan validates; edited plan re-validates; invalid → retry-then-typed |
| 5 | `Ticket_ProvenanceThreaded` | queue entry + trailers + verification record all carry the ticket ref |
| 6 | `Outcome_WriteBackFixtures` | comment body contains PR link/verification/audit refs; transition only when enabled; permission-denied → typed non-fatal |
| 7 | `Epic_ImportAndSyncDiff` | fixture epic → child tasks; tracker change → sync diff flags |
| 8 | `Ticket_EndToEnd` (scripted) | ticket → draft → approve → worker → verified → merged → comment (fixture transport) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** skipping approval for any intake path; provider SDK dependencies / second
transport per provider; clarity check blocking intake; tokens outside the keyring.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Ticket|FullyQualifiedName~ClarityCheck|FullyQualifiedName~Epic"
grep -rn "ticket_" GitLoom.App/ | grep -i "settings.json\|preferences"   # 0 hits
grep -rn "Atlassian.SDK\|linear-sdk" GitLoom.Core/                        # 0 hits — thin REST only
```

---

## 8. Definition of done

- [ ] Five provider clients (audited-transport discipline) + routing rules + clarity check.
- [ ] Draft → editable preview → standard approval → worker with ticket provenance; batch intake.
- [ ] Outcome write-back (comment + optional transition, off by default); epic import + sync.
- [ ] End-to-end scripted test green; all edge rows covered.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-27**, base `phase2`.
