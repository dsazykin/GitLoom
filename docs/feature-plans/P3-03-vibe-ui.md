# P3-03 — Vibe UI: Mode Toggle, Chat, Live Preview — Implementation Plan

**Task ID:** P3-03 · **Milestone:** M9 · **Priority:** P0
**Depends on:** P3-01 (checkpoints), P3-02 (triage), P2-13 (dock shell), P2-33
(`LivePreviewControl` + port forwards).
**Branch:** implement on `feature/P3-03-vibe-ui` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P3-03 of `docs/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §K-4). **Mode is a view-state, not a data migration** — and never an installer fork.

---

## 0. Context — what exists today

Everything under the UI exists: the orchestrator engine (P2-26) emits status events, P3-01
checkpoints, P3-02 triage cards, P2-33 shipped `LivePreviewControl` behind a flag with
daemon-managed port forwards. This task is the Vibe presentation layer: a 2-pane Chat +
LivePreview mode collapsing the developer dock, with the terminal demoted to "technical details".

### What you can rely on

| Fact | Where |
|---|---|
| Dock workspaces + layout persistence | P2-13 |
| Chat bridge RPC + orchestrator events (`APP_READY`, checkpoint, verifying, escalated) | P2-26 |
| `LivePreviewControl` (origin-guarded WebView, forwarded ports) | P2-33 |
| Triage view/cards | P3-02 |
| Terminal view (unchanged, embedded behind the expander) | P2-03/P2-18 |
| Journaled/audited service paths (Vibe must reuse them — no shortcuts) | P2-10/P2-15/T-19 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.App/ViewModels/Vibe/VibeWorkspaceViewModel.cs` (mode host: chat + preview panes) |
| **Create** | `GitLoom.App/ViewModels/Vibe/VibeChatViewModel.cs` (+ `ChatCardViewModel` hierarchy: message, checkpoint, verifying, escalation, app-ready cards) |
| **Create** | `GitLoom.App/Views/Vibe/VibeWorkspaceView.axaml(.cs)`, `VibeChatView.axaml(.cs)` |
| **Create** | `GitLoom.App/Services/UiModeService.cs` (Developer ⇄ Vibe view-state; persisted preference; per-window) |
| **Edit** | `MainWindowViewModel`/`MainWindow.axaml` — mode switch control; dock collapse/restore |
| **Edit** | `LivePreviewControl` usage — navigate on `[APP_READY_ON_PORT_X]` via the forwarded port; port-picker chip for multiple ports |
| **Create** | `GitLoom.Tests/UiModeToggleTests.cs`, `ChatCardRenderingTests.cs`, `VibePreviewNavigationTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **In-app mode switch** (never an installer fork) collapsing the developer dock into a 2-pane
  **Chat + LivePreview** layout.
- `LivePreviewControl` navigates on `[APP_READY_ON_PORT_X]` through the localhost bridge
  port-forward; hot reload works because dev server + sources share ext4 — the preview just
  points at the forwarded port.
- Chat renders orchestrator status events as **friendly cards** (checkpoint created, verifying,
  escalation); the terminal remains available behind **"Show technical details"**.
- Toggling back to Developer Mode restores the full dock **with the same session intact**.

---

## 3. Implementation steps

1. **`UiModeService`:** view-state enum per window; switching swaps the content region between
   the P2-13 dock host and `VibeWorkspaceView` — the underlying session objects (agent, streams,
   terminal VM) are shared, not recreated (invariant: no data migration). Preference persisted;
   mid-generation toggle never interrupts the agent (edge row 3 — the streams are consumers,
   not owners).
2. **Chat pane:** message composer (reuses P2-40 conveniences where flagged) → chat bridge RPC;
   incoming: agent text (rendered markdown-lite) + status events mapped to card VMs:
   - checkpoint card ("Saved your progress — <summary>", restore affordance via P3-01),
   - verifying card (spinner → green/red),
   - app-ready card ("Your app is running" + open-preview button),
   - escalation card → opens P3-02 triage.
   Copy comes from a deck (extend `EscalationCopy` pattern — no hardcoded strings).
3. **Preview pane:** hosts `LivePreviewControl`; on `AppReady(port)` navigate to the forwarded
   endpoint (P2-33 manager). Multiple ports → picker chips (edge row 1). Navigation crash →
   reload affordance; session unaffected (edge row 2). The P2-33 feature flag flips to default-on
   for Vibe mode.
4. **Technical details:** expander/drawer hosting the live terminal view (read-only input
   optional per mode policy — Vibe sessions are locked like coordinator-managed workers, P2-14
   interceptor) + the plan tree (P2-39).
5. **No privileged shortcuts:** every Vibe action (restore, merge-equivalent "publish" later,
   checkpoint) routes through the same journaled/audited services as developer mode — review the
   VM call sites against this rule (invariant 2).
6. **Theming:** Vibe layout in all five themes; larger type ramp per design tokens (no raw
   values).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| multiple dev servers/ports | port picker chip |
| preview navigation crash | reload affordance, session unaffected |
| mode toggle mid-generation | no interruption to the agent (stream continuity asserted) |
| toggle Developer → Vibe → Developer | dock layout restored; same session objects (reference equality) |
| escalation while in Vibe | triage card + screen (P3-02), chat history preserved |

---

## 5. Invariants (MUST)

1. Mode is a **view-state, not a data migration**.
2. Every Vibe action routes through the same journaled/audited services as developer mode — no
   privileged shortcut paths.
3. The terminal is always reachable ("Show technical details") — Vibe hides, never removes.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `ScaffoldFlow_ReadyEventNavigatesPreview` | scripted dev server (P2-26 fixture) → `[APP_READY]` → preview VM navigates to the forwarded port |
| 2 | `ChatCards_FromEventFixtureStream` | fixture event stream → exact card sequence/types/copy keys |
| 3 | `Toggle_RoundTripPreservesSession` (headless) | Developer → Vibe → Developer: same session VM instances, dock layout restored, terminal scrollback intact |
| 4 | `Toggle_MidGeneration_NoInterrupt` | streaming fixture during toggle → zero dropped frames/events |
| 5 | `MultiPort_PickerChips` | two ready events → chips; selection switches preview |
| 6 | `PreviewCrash_ReloadAffordance` | simulated WebView crash → reload button, agent stream unaffected |
| 7 | `VibeActions_UseJournaledPaths` | restore from a checkpoint card → P3-01 service invoked (spy), journal entry exists |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a separate Vibe data model/session store; installer-level mode forks; Vibe-only
service shortcuts; hardcoded copy; terminal removal.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~UiModeToggle|FullyQualifiedName~ChatCard|FullyQualifiedName~VibePreview"
grep -rn "new AgentSession\|new TerminalViewModel" GitLoom.App/ViewModels/Vibe/   # 0 hits — shared session objects
```

---

## 8. Definition of done

- [ ] In-app mode toggle (view-state, persisted, mid-generation-safe) collapsing to Chat + LivePreview.
- [ ] Chat cards for all orchestrator events with deck copy; triage integration; technical-details drawer.
- [ ] Preview navigation on ready events, multi-port chips, crash recovery.
- [ ] All edge rows green (headless harness). `AGENTS.md` Repository Map updated. One task = one PR linking **P3-03**, base `phase2`.
