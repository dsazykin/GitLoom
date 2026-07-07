# P3-10 — Team Collaboration Layer — Implementation Plan

**Task ID:** P3-10 · **Milestone:** M10 · **Priority:** P1 for the Team tier (the "expansion
product" glue).
**Depends on:** P2-15 (audit), P2-16 (SIEM/export), P2-23 (identity/roles), P3-06 (tenant
store for the server-side dashboard).
**Branch:** implement on `feature/P3-10-team-collab` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P3-10 of `docs/GitLoom_Master_Implementation_Document_v2.md`.
> **v1 scope is deliberately thin:** shared queue view, org dashboard, policy distribution —
> no real-time multiplayer canvases (an explicit §9 skip).

---

## 0. Context — what exists today

Everything is single-user: one daemon, one human gate. Teams need shared visibility of agent
work, review assignment, and org-wide governance reporting. The primitives exist: identities and
roles (P2-23), receipts and review state (P2-38), audit + export (P2-15/16), tenant store
(P3-06). This task glues them into a thin team surface.

### What you can rely on

| Fact | Where |
|---|---|
| OIDC identities, roles, permission interceptors | P2-23 |
| `AwaitingReview` state + review sessions + receipts per identity | P2-10/P2-38 |
| Spend telemetry, verification records, review-latency raw data | P2-08/P2-10/P2-38 |
| Signed policy doc + hot reload | P2-23 |
| Tenant store + cloud pods (server-side aggregation) | P3-06 |
| Host permissions as the access source of truth (repo membership) | T-23/host APIs |
| Local export patterns (SIEM/coverage exports) | P2-16/P2-38 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Team/SharedQueueService.cs` (opt-in per repo: publish/subscribe `AwaitingReview` branch summaries; assignment/request-review) |
| **Create** | `GitLoom.Core/Team/RepoAccessGate.cs` (host-permission check before any shared metadata is served to a member) |
| **Create** | `GitLoom.Server/Team/OrgDashboardService.cs` (server-side aggregates over the tenant store; desktop-only orgs → local export instead) |
| **Create** | `GitLoom.Core/Team/PolicyTemplateService.cs` (org-template management for the P2-23 signed policy doc) |
| **Create** | `GitLoom.App/ViewModels/Team/SharedQueueViewModel.cs`, `OrgDashboardViewModel.cs`, `PolicyTemplatesViewModel.cs` + views |
| **Edit** | protos (shared-queue pub/sub, assignment RPCs, dashboard queries) |
| **Create** | `GitLoom.Tests/SharedQueueTests.cs`, `RepoAccessGateTests.cs`, `OrgDashboardAggregateTests.cs`, `ReviewAssignmentAuditTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding, v1 scope)

- **Shared queue view:** org members see each other's `AwaitingReview` branches (**opt-in per
  repo**); assign/request review; **reviewer identity lands in the audit chain**.
- **Org dashboard:** aggregate spend (P2-08 telemetry), verification pass rates, review latency,
  audit-export status — **server-side over the P3-06 tenant store; desktop-only orgs get a local
  export instead**.
- **Policy distribution:** the P2-23 signed policy doc gains org-template management UI.

**Invariants:** collaboration metadata **never includes repo content** for members without repo
access (host permissions are the source of truth); **all sharing is opt-in per repo**.

---

## 3. Implementation steps

1. **Sharing model:** per-repo opt-in flag (off by default, invariant). What is shared =
   **metadata only**: branch name, agent/model, task title, state, verification pass/fail +
   freshness, spend, assignee — never diffs, file paths beyond the repo name, or evidence
   excerpts (invariant: no content without access).
2. **`RepoAccessGate`:** before serving shared entries (or any drill-down) to a member, verify
   the member's host access to that repo (host API membership/permission check, cached with TTL;
   host permissions are the source of truth). No access → the repo's entries are invisible, not
   redacted (edge row 1). Members with access who open an entry get the full P2-11 cockpit
   against their own fetch of the branch — content always flows via Git/host, never via the
   collaboration channel.
3. **Shared queue:** publish on `AwaitingReview` transitions (opt-in repos only) into the org
   channel (tenant store for cloud orgs; a daemon-to-daemon sync is out of scope in v1 —
   desktop-only orgs see the shared view only where a common tenant/cloud store exists, else the
   feature honestly requires cloud). Assignment: `RequestReview(branch, member)` /
   `Assign(member)` → notification (P2-32 routing) + the assignee's needs-attention lane
   (P2-C3/P2-13); acting reviewer's identity lands in receipts (P2-38 already carries identity)
   and the audit chain (`review_assigned`, `review_requested` events).
4. **Org dashboard:** `OrgDashboardService` aggregates over the tenant store: spend by
   repo/agent/model/day (P2-08 rows), verification pass rates (P2-10 records), review latency
   (`AwaitingReview` → merge/reject deltas), audit-export/SIEM delivery status (P2-16 cursors),
   coverage summary (P2-38 reports). Desktop-only orgs: the same aggregates computed locally and
   written as an export file (reuse P2-16/P2-38 export patterns) — no server dependency for the
   numbers, only for the shared live view.
5. **Policy templates:** CRUD over org policy templates (model allowlists, egress rules,
   budgets, approval classes) → sign → distribute via the P2-23 pipeline; template versioning +
   diff view before publish; publishes audited.
6. **UI:** shared queue (grouped by repo, assignment affordances), dashboard (dataviz per the
   design system tokens), policy templates editor.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| member without host access to a shared repo | entries invisible (not redacted rows); drill-down denied server-side |
| repo sharing toggled off with live assignments | entries retract; assignments cancelled with notification |
| assignment to a member without access | typed refusal at assign time |
| desktop-only org opens the dashboard | local export path; no server errors |
| host permission revoked mid-session (cache TTL) | next access check denies; cached window documented |
| two reviewers on one branch | both receipts/identities recorded (P2-38 semantics) |

---

## 5. Invariants (MUST)

1. Collaboration metadata never includes repo content for members without repo access — host
   permissions are the source of truth, enforced server-side (`RepoAccessGate`), not in the UI.
2. All sharing is opt-in per repo (default off).
3. Review assignments/identities land in the audit chain.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `PermissionBoundary_NoContentLeak` | member without access: shared list excludes the repo; direct RPC probe denied; no metadata beyond nothing |
| 2 | `OptIn_DefaultOffAndRetract` | default unshared; toggle off retracts + cancels assignments |
| 3 | `Assignment_AuditAndNotification` | request/assign → audit events with identities + routed notification |
| 4 | `Dashboard_AggregatesFromFixtures` | fixture telemetry → exact spend/pass-rate/latency/export-status numbers |
| 5 | `DesktopOnly_LocalExport` | no tenant store → export file with the same aggregate schema |
| 6 | `PolicyTemplate_SignPublishAudit` | template → signed doc → distributed (P2-23 hot reload) → audited |
| 7 | `AccessGate_CacheTtlRevocation` | revoked permission → denied after TTL expiry |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** content (diffs/paths/evidence) in collaboration payloads; sharing on by default;
UI-only access checks; a parallel policy pipeline; real-time canvas scope creep.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~SharedQueue|FullyQualifiedName~RepoAccessGate|FullyQualifiedName~OrgDashboard|FullyQualifiedName~ReviewAssignment"
grep -rn "Patch\|DiffHunk" GitLoom.Core/Team/    # 0 hits — metadata only
```

---

## 8. Definition of done

- [ ] Opt-in shared queue (metadata-only) with server-side access gating on host permissions.
- [ ] Assignment/request-review with audited identities + notifications into the attention lanes.
- [ ] Org dashboard (server-side over the tenant store; local export for desktop-only orgs).
- [ ] Policy template management on the P2-23 pipeline.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P3-10**, base `phase2`.
