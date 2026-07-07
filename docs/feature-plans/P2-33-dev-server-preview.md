# P2-33 — In-App Dev-Server Preview & Port Panel — Implementation Plan

**Task ID:** P2-33 · **Milestone:** M7.75 · **Priority:** P2-parity (Conductor browser preview,
Superset in-app browser/ports).
**Depends on:** P2-26 (port harvesting exists there).
**Branch:** implement on `feature/P2-33-dev-server-preview` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-33 of `docs/GitLoom_Master_Implementation_Document_v2.md`. The embedded
> preview control is P3-03's `LivePreviewControl` **shipped early behind a flag** — build it here
> as a reusable control so P3-03 consumes it unchanged.

---

## 0. Context — what exists today

P2-26 emits `[APP_READY_ON_PORT_X]` events when an agent's dev server comes up inside its
sandbox. The port is only reachable inside the VM's network namespace. This task surfaces the
ports as chips on agent cards, manages daemon-side port-forwards (sandbox → Windows-reachable
localhost), and offers an embedded preview pane or system-browser open.

### What you can rely on

| Fact | Where |
|---|---|
| `AppReady(port)` events per session | P2-26 `VibeOrchestrator` event stream |
| Sandbox network topology (internal network; proxy container) | P2-07 |
| Agent cards (chips surface) | P2-13 `AgentCardViewModel` |
| Teardown events (forwards must close with the agent) | P2-09 |
| Scheme-validated browser launcher | `GitLoom.App/Services/BrowserLauncher.cs` |
| Headless Avalonia harness (navigation smoke) | TI-00 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Vibe/PortForwardManager.cs` (daemon: sandbox port → VM localhost → Windows-reachable endpoint; lifecycle) |
| **Edit** | proto: `VibeService`/agent metadata — active ports + forward endpoints per agent |
| **Create** | `GitLoom.App/Controls/LivePreviewControl.cs` (embedded WebView wrapper behind a feature flag; reused by P3-03) |
| **Create** | `GitLoom.App/ViewModels/Agents/PortPanelViewModel.cs` (+ chips on `AgentCardViewModel`) + `Views/Agents/PortPanelView.axaml(.cs)` |
| **Create** | `GitLoom.Tests/PortForwardLifecycleTests.cs`, `PortChipProjectionTests.cs`, `PreviewNavigationSmokeTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

Per-agent detected dev-server ports (P2-26 taps) surface as **chips on the agent card**; click →
**embedded preview pane** (the `LivePreviewControl`, flagged) or **system browser**;
**port-forward managed by the daemon** (sandbox → localhost bridge).

---

## 3. Implementation steps

1. **`PortForwardManager`:** on `AppReady(port)` for agent A: establish a forward — a small TCP
   relay in the daemon listening on a VM ephemeral port bound to the VM's Windows-reachable
   interface (`localhost` via WSL2 localhost forwarding), relaying to the agent container's IP:port
   on the internal network (the daemon has a leg on that network; the egress proxy is for
   *outbound* traffic — this is inbound preview traffic and must **not** open general ingress:
   relay binds loopback only). Track `(agentId, containerPort) → forwardEndpoint`; idempotent per
   pair; close on agent teardown/port disappearance.
2. **Metadata plumbing:** forwards published in agent metadata/events; `PortPanelViewModel`
   projects them; chips appear/disappear live on the cards.
3. **`LivePreviewControl`:** WebView (Avalonia `NativeWebView`/`WebView` package chosen per
   current ecosystem state — pin the choice, wrap it fully so P3-03 and a future swap don't leak
   the dependency) with address bar (read-only origin), refresh, open-in-browser button (through
   `BrowserLauncher`). Behind `Features.LivePreview` flag; flag off → chips open the system
   browser only.
4. **Navigation guard:** the preview navigates only within the forwarded origin
   (`http://localhost:<fwd>`); external navigations route to the system browser (scheme-validated).
5. **Teardown:** subscribe agent-terminated events → close forwards; `PortForwardManager.Dispose`
   closes all listeners (residue test).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| dev server restarts on a new port | old chip/forward retired, new one appears |
| agent teardown with active forwards | forwards closed; no orphan listeners |
| two agents on the same container port | distinct forwards, no collision |
| preview navigation to an external URL | opens in system browser, pane stays on origin |
| flag off | chips present, click → system browser only |

---

## 5. Invariants (MUST)

1. Forward listeners bind loopback only — no LAN exposure (that is P2-41's authenticated job).
2. Forward lifecycle is tied to the agent lifecycle (teardown closes forwards).
3. The WebView dependency stays inside `LivePreviewControl` (P3-03 reuse; no leaks into VMs).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Forward_EstablishRelayRoundTrip` (`RequiresDocker`) | container HTTP server → forward endpoint serves the content |
| 2 | `Forward_TeardownClosesListeners` | agent disposal → sockets closed (poll the port) |
| 3 | `Forward_PortChangeRetiresOld` | new `AppReady` port → old endpoint dead, new live |
| 4 | `Chips_ProjectFromFixtureEvents` | fixture event stream → chip set on the card VM |
| 5 | `Preview_NavigationSmoke` (headless) | control loads a local fixture page; external link → launcher invoked (spy), origin unchanged |
| 6 | `Forward_LoopbackOnly` | listener endpoints all loopback |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** binding forwards to non-loopback interfaces; WebView types outside the control;
forwards surviving teardown; bypassing `BrowserLauncher` for external opens.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~PortForward|FullyQualifiedName~PortChip|FullyQualifiedName~PreviewNavigation"
grep -rn "WebView" GitLoom.App/ViewModels/    # 0 hits — control-internal only
```

---

## 8. Definition of done

- [ ] Daemon port-forward relay (loopback-only, lifecycle-tied, idempotent).
- [ ] Port chips on agent cards + port panel, live from events.
- [ ] `LivePreviewControl` behind the flag (origin-guarded, launcher for external), reusable for P3-03.
- [ ] All edge rows tested. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-33**, base `phase2`.
