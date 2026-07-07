# P3-04 — One-Click Deployment — Implementation Plan

**Task ID:** P3-04 · **Milestone:** M9 · **Priority:** P1
**Depends on:** P3-03 (Vibe UI surface), P2-22 (loopback OAuth).
**Branch:** implement on `feature/P3-04-one-click-deploy` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P3-04 of `docs/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §K-5). **Publish is explicit — never automatic on checkpoint.**

---

## 0. Context — what exists today

Vibe users have a running app in a sandbox; they need "Publish to Web". The pieces exist:
authenticated push (T-14 path), GitHub repo creation (T-23 transport), loopback OAuth (P2-22),
triage (P3-02), auto-checkpoints (P3-01). This task adds the deploy-provider abstraction (Vercel
+ Netlify) and the publish flow.

### What you can rely on

| Fact | Where |
|---|---|
| `LoopbackOAuthListener` + PKCE | P2-22 |
| Keyring conventions (`deploy_<provider>`); header-only tokens (G-13) | P2-01 |
| Authenticated push (existing CLI path) + repo-create via the audited GitHub transport | T-14/T-23 |
| Final checkpoint primitive | P3-01 |
| Triage pattern for failures (redacted log tails) | P3-02 |
| `HttpMessageHandler` seam + recorded fixtures discipline | P2-01/P2-27 providers |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Deploy/IDeployProvider.cs` (`AcquireTokenAsync`, `CreateProject`, `TriggerDeploy`, `PollStatus`, `GetLiveUrl`) |
| **Create** | `GitLoom.Core/Deploy/VercelDeployProvider.cs`, `NetlifyDeployProvider.cs` (thin REST, one audited transport each) |
| **Create** | `GitLoom.Core/Deploy/PublishService.cs` (checkpoint → push → trigger → poll → URL; failure → triage) |
| **Create** | `GitLoom.App/ViewModels/Vibe/PublishViewModel.cs` + flow UI (provider pick, progress stages, live-URL card) |
| **Create** | `GitLoom.Tests/DeployProviderFixtureTests.cs`, `PublishServiceTests.cs`, `DeployTokenStorageTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- Vercel + Netlify behind `IDeployProvider` — `AcquireTokenAsync` via **loopback + PKCE**;
  `CreateProject`, `TriggerDeploy`, `PollStatus`, `GetLiveUrl`.
- **"Publish to Web"** = final auto-checkpoint → push to the user's GitHub repo (existing
  authenticated push path) → trigger → poll → present the live URL.
- Failures route into **P3-02's triage pattern** (never raw provider logs by default).
- Tokens keyring-only (`deploy_<provider>`).

---

## 3. Implementation steps

1. **Providers:** thin REST clients (Vercel API v13 deployments / Netlify API), one audited
   transport each — token header-only, `RedactionExtensions` on errors, `HttpMessageHandler`
   seam, typed failures (auth, quota, build-failed with log-tail pointer). OAuth where the
   provider supports the loopback flow (`AcquireTokenAsync` → P2-22 listener); PAT fallback
   dialog for providers/plans without it (token straight to keyring).
2. **`PublishService.PublishAsync(repoHash, agentId, provider, ct)` stages:**
   1. **Checkpoint:** final auto-checkpoint (P3-01) labeling the publish.
   2. **Repo:** existing GitHub repo remote? push the agent branch/main per the Vibe merge
      policy via the **existing authenticated push path**. First publish with no repo →
      create-repo flow via the T-23 transport (name suggestion from the project; private by
      default) then push (edge row 1).
   3. **Project:** `CreateProject` (idempotent — re-publish reuses the linked project, edge
      row 3) wired to the GitHub repo (both providers deploy-from-git).
   4. **Deploy:** `TriggerDeploy` → `PollStatus` (bounded, backoff) → `GetLiveUrl`.
   Stage progress streams to the `PublishViewModel` (friendly stage cards, copy-deck strings).
3. **Failure routing:** build failure → the P3-02 triage pattern: friendly headline + actions
   (retry / get help with a **redacted** provider-log tail attached to the bundle); raw logs
   only behind "Show technical details" (edge row 2).
4. **Explicitness:** publish runs only from the user's button press — never triggered by
   checkpoints or automations (invariant 2); the action is audited (`publish_triggered`,
   outcome event with URL/failure).
5. **UI:** provider picker (icons, connect state), progress stages, success card (live URL,
   copy/open via the validated launcher), re-publish button.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| first publish (no repo yet) | create-repo flow via the T-23 transport, then push |
| build failure | triage with provider log tail attached (redacted) |
| re-publish | same project, new deploy |
| OAuth denial/timeout | typed, nothing stored, flow restartable |
| poll timeout (provider stuck) | typed timeout → triage; no infinite poll |
| token revoked upstream | typed auth failure → reconnect affordance |

---

## 5. Invariants (MUST)

1. No token in argv/URL/log (G-13) — keyring only (`deploy_<provider>`).
2. Publish is explicit — never automatic on checkpoint.
3. One audited transport per provider; failures redacted before any UI/bundle surface.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Provider_FixtureMatrix` (per provider) | create/trigger/poll/url/fail against recorded fixtures; headers token-only; errors redacted |
| 2 | `Publish_FullFlowFixture` | checkpoint → push (fixture remote) → project → deploy → URL; stage events in order |
| 3 | `Publish_FirstTime_CreatesRepo` | no remote → repo created via T-23 fixture transport → push |
| 4 | `Publish_BuildFailure_Triage` | failing deploy fixture → triage payload with redacted log tail |
| 5 | `Republish_ReusesProject` | second publish → no `CreateProject` call (spy), new deploy |
| 6 | `TokenStorage_Audit` | tokens only in keyring; grep argv/urls/logs seams |
| 7 | Live end-to-end (network-gated trait) | real test account: publish → reachable URL (manual/CI-gated) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** provider SDK dependencies; tokens outside the keyring; auto-publish paths; raw
provider logs by default; a second push implementation.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~DeployProvider|FullyQualifiedName~PublishService|FullyQualifiedName~DeployToken"
grep -rn "deploy_" GitLoom.App/ | grep -i "settings.json\|log"   # 0 hits
```

---

## 8. Definition of done

- [ ] `IDeployProvider` + Vercel/Netlify (fixtures, redaction, OAuth/PAT).
- [ ] Explicit publish flow: checkpoint → push/create-repo → project → deploy → live URL; audited.
- [ ] Failure → triage with redacted tails; re-publish reuse.
- [ ] All edge rows green (+ network-gated live smoke). `AGENTS.md` Repository Map updated. One task = one PR linking **P3-04**, base `phase2`.
