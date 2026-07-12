# P2-36 — Governed Lessons: Learning from Rejected Work — Implementation Plan

**Task ID:** P2-36 · **Milestone:** M7.75 · **Priority:** P1 (matches MergeLoom "self-learning",
auditable).
**Depends on:** P2-11 verdicts, P2-15 audit, P2-34 vault.
**Branch:** implement on `feature/P2-36-governed-lessons` off `phase2`; PR targets `phase2`.

> **Verification profile:** Fully automated — propose/enable/inject round-trips, dedup, secret-scan rejection, digest pinning, audit events; no human step.
>
> **Source of truth:** §P2-36 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat to preserve:** glass-box governed learning — every lesson has a source reference,
> requires a human enable (or audited org auto-enable policy), lives in repo history, and its
> enable/disable events are hash-chained.

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
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-36 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-36** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-36 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

Rejections, verification failures, acknowledged flags, and AI-review findings are all recorded
but never fed back to future workers. This task turns them into **lessons**: reviewable one-liner
rules stored in a versioned repo file (`.gitloom/lessons.md`), enabled explicitly, prepended into
every context pack.

### What you can rely on

| Fact | Where |
|---|---|
| Rejection/flag-acknowledgment events | P2-10/P2-11 |
| AI-review findings events | P2-35 |
| Context packs + `RulesDigest` pinning | P2-34 |
| Audit chain (`Append` with identity) | P2-15 |
| Secret scanner (T-30 `IPreCommitScanner`) | `GitLoom.Core/Services/PreCommitScanner.cs` |
| Foreground repo write path (the lessons file is a normal repo file, committed by the human/PR) | existing Git services |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Context/LessonsService.cs` (`ILessonsService`, `Lesson`) |
| **Create** | `GitLoom.Core/Context/LessonsFile.cs` (parse/serialize `.gitloom/lessons.md` — human-editable markdown with stable ids) |
| **Create** | `GitLoom.Core/Context/LessonSources.cs` (subscribers: verification failure, review rejection, acknowledged flag, AI finding → proposals) |
| **Edit** | `GitLoom.Core/Context/PackBuilder.cs` (enabled lessons prepended; digest includes lessons) |
| **Create** | `GitLoom.App/ViewModels/Context/LessonsViewModel.cs` + view (pending proposals, enable/disable, source links) |
| **Create** | `GitLoom.Tests/LessonsFileTests.cs`, `LessonsServiceTests.cs`, `LessonDedupTests.cs`, `LessonSecretScanTests.cs`, `LessonDigestPinningTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (must exist exactly)

```csharp
// GitLoom.Core/Context/LessonsService.cs
public sealed record Lesson(string Id, string Text, string SourceKind /* rejection|failure|ai-review|manual */,
    string SourceRef, DateTimeOffset When, bool Enabled);
public interface ILessonsService
{
    IReadOnlyList<Lesson> GetLessons(string repoHash);
    Lesson Propose(string repoHash, string text, string sourceKind, string sourceRef); // enters review state
    void SetEnabled(string repoHash, string lessonId, bool enabled, string osIdentity);
}
```

Lessons live as a **versioned file in the repo** (`.gitloom/lessons.md`, human-editable, PR-able)
plus daemon state for pending proposals. Enabled lessons are prepended into every context pack
(the P2-34 `RulesDigest` covers them).

---

## 3. Implementation steps

1. **File format:** markdown list with stable ids and metadata comments:

   ```markdown
   <!-- gitloom:lessons v1 -->
   - [x] `L-3f2a` Always run the full suite when touching GitServices.cs
     <!-- source: rejection audit:1234 2026-07-07 -->
   - [ ] `L-9c1d` Prefer ExecuteWithRepo over raw Repository handles
     <!-- source: ai-review finding:88 2026-07-08 -->
   ```

   `[x]` = enabled. `LessonsFile` parses/serializes round-trip-stable (hand edits preserved);
   id = short content hash prefix. Repo without `.gitloom/` → created on first enable (edge
   row 3).
2. **Proposals:** `LessonSources` subscribes the four event kinds; each proposal:
   - derives a candidate one-liner (rejection reason text, failing invariant, acknowledged flag
     description, AI finding text — bounded length);
   - **dedup by content hash** against existing lessons + pending proposals (edge row 1);
   - **T-30 secret scan** on the text — hits ⇒ typed rejection of the proposal (invariant 2);
   - lands in daemon pending state (never auto-written to the repo file).
3. **Enable flow:** `SetEnabled(…, osIdentity)`: pending proposal → written into
   `.gitloom/lessons.md` in the **Windows repo working tree** (the human commits/PRs it — the
   daemon never commits to the user's repo); toggle events → audit chain
   (`lesson_enabled`/`lesson_disabled` with identity + lesson id + source ref). **No
   auto-enable without an explicit org policy** (P2-23 policy field; auto-enables audited with
   `approver=policy:*`).
4. **Injection:** `PackBuilder` prepends enabled lessons (as rules) — `RulesDigest` = hash over
   rule files **+ enabled lessons**; packs pin the digest they shipped with (P2-34 invariant —
   mid-task lesson changes don't mutate in-flight packs).
5. **UI:** pending proposals (text editable before enable, source link → audit entry/verdict),
   enabled list with disable toggles, per-repo. A lesson whose source audit entry was redacted
   keeps working — reference is by id (edge row 2).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| duplicate lessons | deduped by content hash (proposal rejected as duplicate) |
| lesson referencing a redacted audit entry | keeps working (reference by id) |
| repo without `.gitloom/` | created on first enable |
| hand-edited lessons file | parser preserves edits; ids stable; unknown lines untouched |
| lesson text containing a secret | T-30 scan rejects the proposal, typed |
| lesson toggled mid-task | in-flight packs keep their digest; next pack reflects it |

---

## 5. Invariants (MUST)

1. No lesson auto-enables without an explicit org policy (audited when it does).
2. Lessons never contain secrets (T-30 scan on propose).
3. Packs pin the lessons digest they shipped with.
4. The daemon never commits the lessons file — humans do (PR-able by design).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `File_RoundTripStable` | parse→serialize preserves hand edits, comments, order |
| 2 | `Propose_EnableInject_RoundTrip` | proposal → enable(identity) → file updated + audit events → next pack contains the lesson + new digest |
| 3 | `Dedup_ByContentHash` | same text twice → second rejected |
| 4 | `SecretScan_Rejects` | seeded token in text → typed rejection |
| 5 | `DigestPinning` | toggle mid-task → in-flight pack digest unchanged; new pack differs |
| 6 | `RedactedSource_StillWorks` | source entry redacted → lesson resolves, link renders tombstone |
| 7 | `AutoEnable_PolicyGated` | policy off → stays pending; on → enabled with `approver=policy:*` audit |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** daemon committing to the user's repo; silent auto-enable; a second lessons store
bypassing the file; unscanned proposals.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Lesson"
grep -rn "Commit(" GitLoom.Core/Context/LessonsService.cs   # 0 hits — file writes only
```

---

## 8. Definition of done

- [ ] `.gitloom/lessons.md` format (stable ids, round-trip-safe) + `ILessonsService` per contract.
- [ ] Four proposal sources with dedup + secret scan; human/policy enable with audit chaining.
- [ ] Pack injection with digest pinning; UI for proposals/toggles with source links.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-36**, base `phase2`.
