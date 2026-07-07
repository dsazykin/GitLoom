# P3-05 — GitLoom Web: the Hosted Vibe Delivery — Implementation Plan

**Task ID:** P3-05 · **Milestone:** M9/M10 · **Priority:** P1 (the segment's browser-native
expectation; a local install cannot win time-to-first-magic).
**Depends on:** P3-03 (Vibe UI patterns), P3-06/P2-25 (cloud daemon pod).
**Branch:** implement on `feature/P3-05-gitloom-web` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P3-05 of `docs/GitLoom_Master_Implementation_Document_v2.md`. This task
> is explicitly **the spike + walking skeleton** — architecture-level contract; the polish
> backlog follows once the skeleton stands.

---

## 0. Context — what exists today

The G-14 discipline (transport-agnostic protos, P2-25 WAN CI) exists precisely so this product
can exist without protocol changes: a browser shell speaking the same gRPC-web contract to a
cloud daemon pod (P3-06). P2-41 already built a daemon-served SPA (board/approvals); this task
builds the hosted **Vibe** experience: chat + live preview from a cloud session, no local
install, with desktop session adoption.

### What you can rely on

| Fact | Where |
|---|---|
| Unchanged proto contract passing at 80 ms RTT (CI) | P2-25 |
| Cloud pod: daemon image, mTLS front door, tenancy, repo sync | P3-06 |
| TS SDK / grpc-web plumbing + SPA build pipeline | P2-32 / P2-41 |
| Vibe chat/preview UX patterns + copy decks | P3-03 |
| OIDC identity + roles | P2-23 |
| Host-provider OAuth flows (web variant needed) | T-14 / P2-22 |
| Audit chain (web actions must land in it) | P2-15 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Web/` (ASP.NET Core host serving the shell; session routing to pods) |
| **Create** | `web/shell/` (TS shell — reuse the P2-41 SPA stack; screens: repo connect, session chat, live preview) |
| **Create** | `GitLoom.Web/Sessions/SessionRouter.cs` (user → tenant pod → session; cookie-scoped) |
| **Create** | `GitLoom.Web/Preview/PreviewProxy.cs` (per-session preview origin, proxied through the pod egress, auth cookie) |
| **Create** | web-variant host OAuth (authorization-code, no loopback — standard web redirect on `GitLoom.Web`) |
| **Create** | desktop adoption: `GitLoom.App` "Open web session" flow (session handle → local workspace attach) + proto addition |
| **Create** | `GitLoom.Tests/WebContractTests.cs`, `PreviewOriginIsolationTests.cs`, `AdoptSessionTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding, architecture-level)

- `GitLoom.Web` — ASP.NET Core host serving a Blazor/WASM **or thin TS** shell (decide in the
  spike; the P2-41/P2-32 TS stack is the default candidate) speaking the **same gRPC-web
  contract** to a cloud daemon pod — zero proto changes.
- **Session** = one cloud worktree pod (P3-06) running the Vibe orchestrator; **preview proxied
  through the pod's egress with an auth cookie**; chat over the existing event stream.
- **Identity:** P2-23 OIDC; repos connected via host-provider OAuth (web variant).
- The desktop app can **adopt** a web session (open it as a local workspace) — the continuity
  story.

---

## 3. Implementation steps (spike → skeleton)

1. **Spike (time-boxed, ADR output):** shell technology decision (thin TS vs Blazor/WASM) —
   criteria: reuse of the P2-32 SDK + P3-03 copy decks, bundle size, gRPC-web streaming
   ergonomics. Record as `docs/adr/web-shell.md`.
2. **`GitLoom.Web` host:** serves the shell; authenticates via OIDC (P2-23 web flow); session
   cookie (`Secure; HttpOnly; SameSite`) is the **only** browser-persisted credential —
   everything else stays server-side (rejection trigger: secrets in browser storage beyond the
   cookie).
3. **`SessionRouter`:** user → tenant pod (P3-06 provisioning API) → create/attach a Vibe
   session; the shell's gRPC-web calls proxy through `GitLoom.Web` to the pod's mTLS front door
   (the browser never holds pod credentials).
4. **Shell screens:** connect repo (host OAuth web-redirect variant → repo picker) → session
   chat (P3-03 card model over the event stream) → live preview pane.
5. **`PreviewProxy`:** each session's dev-server preview is served from a **per-session origin**
   (`<session-id>.preview.<domain>` or path-scoped with strict CSP + sandboxed iframe) proxied
   through the pod's egress; requests carry the session auth cookie; cross-session requests
   rejected (isolation test).
6. **Desktop adoption:** "Open in desktop" mints a session handle (deep link `gitloom://` —
   non-secret, P2-22 rules: the handle is an opaque id, auth happens locally via the user's
   credentials); the desktop attaches its `DaemonClient` to the cloud pod (P3-06
   `CloudCredentialProvider`) and opens the session as a workspace — round-trip test.
7. **Audit:** every web session action lands in the tenant pod's chain exactly like desktop
   actions (identity = OIDC subject; invariant 3).

---

## 4. Edge-case matrix (binding)

| Case | Required behavior |
|---|---|
| pod cold-start on first session | shell shows staged progress (reuse bootstrap-checklist pattern); no timeout crash |
| cookie expired mid-session | re-auth redirect; session resumes (pod state intact) |
| preview iframe attempts parent access | sandboxed; CSP blocks; test proves no cross-session bleed |
| adoption while the web tab stays open | both clients attached (same session semantics as two desktop windows) |
| tenant without a provisioned pod | provisioning flow, honest wait states |

---

## 5. Invariants (MUST)

1. **No web-only fork of orchestrator logic** — the pod runs the same daemon binary
   (G-14/P2-25 acceptance).
2. Preview iframes are sandboxed and served from a **per-session origin** (no cross-session
   bleed).
3. Web session actions land in the **same audit chain** as desktop actions.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `WebContract_UnchangedProtoSuite` | the web shell drives a pod through the unchanged proto suite (the P2-25 suite via the web proxy path) |
| 2 | `PreviewOrigin_Isolation` | two fixture sessions → session A's cookie/origin cannot fetch B's preview |
| 3 | `AdoptSession_RoundTrip` | web-created session → desktop adopt → same agent/queue state visible; actions from both audited |
| 4 | `Cookie_OnlyBrowserSecret` | shell storage audit: localStorage/sessionStorage free of tokens |
| 5 | `Audit_WebIdentity` | web action → chained event with OIDC subject |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second bespoke web protocol; secrets in browser storage beyond the session
cookie; orchestrator logic forks; unsandboxed preview iframes.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~WebContract|FullyQualifiedName~PreviewOrigin|FullyQualifiedName~AdoptSession"
grep -rn "localStorage\|sessionStorage" web/shell/src/ | grep -i "token\|secret"   # 0 hits
```

---

## 8. Definition of done

- [ ] ADR (shell tech) + `GitLoom.Web` host with OIDC + cookie-only browser credential.
- [ ] Session routing to tenant pods over the unchanged contract; chat + preview screens.
- [ ] Per-session preview origins (isolation-tested); desktop adoption round-trip.
- [ ] Audit parity for web actions. `AGENTS.md` Repository Map updated. One task = one PR linking **P3-05**, base `phase2`.
