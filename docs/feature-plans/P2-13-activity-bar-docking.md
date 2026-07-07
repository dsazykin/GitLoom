# P2-13 — Activity Bar & Docking UI — Implementation Plan

**Task ID:** P2-13 · **Milestone:** M7 · **Priority:** P0
**Depends on:** P2-02 (`DaemonClient` connection state, `GatewayService` spend stream),
P2-03 (`TerminalView`).
**Branch:** implement on `feature/P2-13-activity-bar-docking` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-13 of `docs/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.4). v1 UI rules apply unchanged: design tokens via `{DynamicResource}` only,
> component classes over raw colors, five themes, Repository Map current.

---

## 0. Context — what exists today

The app is a single-window Git client (`MainWindowViewModel` + panels). Agents need a workspace
model: per-agent dock layouts (terminal + agent-diff + staging), an activity bar that shows the
whole swarm at a glance, and disciplined teardown (Dock.Avalonia has a documented floating-window
leak — this task owns the mitigation).

### What you can rely on

| Fact | Where |
|---|---|
| `DaemonClient` connection-state enum (`Connected/Degraded/Down`) + `StreamAgentEvents` | `GitLoom.App/Services/DaemonClient.cs` (P2-02) |
| `GatewayService.StreamSpend` + `GetSnapshot` (per-agent spend, queue depth) | P2-08 |
| `TerminalViewModel`/`TerminalView` behind `ITerminalView` | P2-03 |
| Diff + staging panels composable per repo path (agent worktree = a repo path) | `DiffViewerViewModel`, `StagingPanelViewModel` |
| MVVM: CommunityToolkit, `ViewLocator` pairing, no DI | `GitLoom.App/` |
| Design tokens in every `Themes/*.axaml`; classes/icons in `App.axaml` | v1 design system |

New dependency (App): `Dock.Avalonia`. Keep it out of `GitLoom.Core`.

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.App/ViewModels/Agents/ActivityBarViewModel.cs`, `AgentCardViewModel.cs`, `ResourceMonitorViewModel.cs` |
| **Create** | `GitLoom.App/ViewModels/Agents/AgentWorkspaceViewModel.cs` (dock factory: Terminal + agent-diff + staging per agent) |
| **Create** | `GitLoom.App/Views/Agents/ActivityBarView.axaml(.cs)`, `AgentWorkspaceView.axaml(.cs)` |
| **Create** | `GitLoom.App/Services/DockLayoutPersistence.cs` (layout save/restore per agent kind) |
| **Create** | `GitLoom.App/Converters/AgentStatusBrushConverter.cs` (the **one** status→brush mapping) |
| **Create** | `GitLoom.App/Services/AgentNotificationService.cs` (OS notifications on waiting/blocked transitions) |
| **Edit** | `MainWindowViewModel` / `MainWindow.axaml` (activity bar region + workspace host) |
| **Edit** | `Themes/*.axaml` ×5 (new status tokens) + `App.axaml` (classes/icons) |
| **Create** | `GitLoom.Tests/AgentStatusBrushTests.cs`, `ActivityBarOrderingTests.cs`, `AttentionDerivationTests.cs`, `DockTeardownMemoryTests.cs`, `ActivityBarRenderTests.cs` (headless PNG) |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **Workspace:** `Dock.Avalonia` per-agent workspace — Terminal + agent-diff + staging docked
  panes; layout persisted and restored.
- **Activity bar Row 0:** Resource Monitor — VM CPU/RAM sparklines + token-spend counters from
  `GatewayService`; pinned tabs incl. **Coordinator** with an `IsAttentionRequired` pulse.
- **Activity bar Row 1:** virtualized **LIFO** agent list (newest first).
- **Status micro-badges** via one `AgentStatus → Brush` converter using theme tokens (all five
  themes — new tokens added to every `Themes/*.axaml`).
- **OS notifications** on transitions into waiting/blocked; suppressed when the app is
  foregrounded **on that agent**.
- **Teardown discipline:** `IDisposable` workspace VMs, timers stopped, floating dock windows
  closed (the documented Dock.Avalonia leak), `WeakReferenceMessenger` only (no strong
  event-handler webs).

---

## 3. Implementation steps

1. **Status tokens first:** add `AgentStatus.*` color tokens (Working, Verifying, Verified,
   Stale, AwaitingReview, Conflict, RateLimited, Dead, Paused) to all five theme files +
   semantic classes in `App.axaml`. `AgentStatusBrushConverter` resolves token keys — never
   literal brushes.
2. **`ActivityBarViewModel`:** subscribes `StreamAgentEvents` (agent add/remove/state) and
   `StreamSpend`; Row 1 is an `ObservableCollection<AgentCardViewModel>` inserted at index 0
   (LIFO) inside a virtualized `ItemsRepeater`/`ListBox`. `AgentCardViewModel`: name, status
   badge, spend, headroom hint, attention flag.
3. **Attention derivation:** pure helper — attention = state ∈ {AwaitingReview, Conflict,
   Blocked/waiting-on-input} or coordinator has a pending plan approval (P2-14 feeds this
   later; the derivation function ships now and is unit-tested).
4. **Resource monitor:** poll daemon snapshot (VM CPU/RAM — daemon exposes `/proc` readings via
   an existing/gateway RPC) into fixed-length ring buffers rendered as sparkline `Polyline`s;
   token counters from spend stream. Timer owned by the VM, stopped on Dispose.
5. **`AgentWorkspaceViewModel`:** dock factory building Terminal (P2-03), diff (T-13 against
   the agent worktree path via the `gitloom-vm` fetch — read-only), staging panel; layout
   persisted per agent kind via `DockLayoutPersistence` (JSON in appdata). Restore falls back to
   default layout on schema drift.
6. **Notifications:** `AgentNotificationService` — on state transition into waiting/blocked, OS
   toast (Windows notification API via Avalonia's `WindowNotificationManager` fallback if native
   unavailable); suppress when `MainWindow.IsActive && current workspace == that agent`.
7. **Teardown:** workspace Dispose closes floating windows (`DockControl` enumeration), stops
   timers, unsubscribes messenger. Wire agent-terminated events (P2-09) → workspace disposal.

---

## 4. Invariants (MUST)

1. Open/close an agent tab **50×** → stable heap + zero floating windows (blocking memory test).
2. All colors via design tokens (v1 rules); the converter is the only status→brush site.
3. `WeakReferenceMessenger` only; no static strong event handlers to workspace VMs.
4. Activity bar stays responsive with 20 simulated agents (virtualization verified).

---

## 5. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `StatusBrush_MappingComplete` | every `AgentStatus` value → a resolvable token key in every theme (enumerate theme dictionaries) |
| 2 | `ActivityBar_LifoOrdering` | spawn A,B,C → list order C,B,A; removal keeps order |
| 3 | `Attention_Derivation` | table-driven: states/plan-pending → expected flag |
| 4 | `DockTeardown_50x_MemoryStable` | open/close 50× → heap growth under threshold, `DockControl` floating windows == 0 (headless) |
| 5 | `ActivityBar_HeadlessPng_AllThemes` | render bar with 4 fake agents in each of the five themes → PNGs written as artifacts (visual review) |
| 6 | `Notifications_SuppressedWhenForegrounded` | transition while active-on-agent → no toast; otherwise → toast |

---

## 6. Rejection triggers / Reviewer script

**Rejection:** raw colors / `StaticResource` for colors; a second status→brush mapping; timers
not stopped on Dispose; strong messenger subscriptions; dock logic in `GitLoom.Core`.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~AgentStatusBrush|FullyQualifiedName~ActivityBar|FullyQualifiedName~Attention|FullyQualifiedName~DockTeardown"
grep -rn "StaticResource.*Brush\|#[0-9A-Fa-f]\{6\}" GitLoom.App/Views/Agents/ GitLoom.App/ViewModels/Agents/   # 0 hits
grep -rn "Dock.Avalonia\|DockControl" GitLoom.Core/    # 0 hits
```

---

## 7. Definition of done

- [ ] Activity bar (resource monitor, pinned tabs + attention pulse, virtualized LIFO list) streaming live daemon state.
- [ ] Per-agent dock workspaces (terminal/diff/staging) with persisted layouts.
- [ ] Status tokens in all five themes; one converter; OS notifications with foreground suppression.
- [ ] 50× teardown memory test green; headless theme PNGs attached to the PR.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-13**, base `phase2`.
