# Mainguard Control Center — Design Specification (Lane E Part 1)

**Status: DESIGN SPEC — the Phase-2 swarm command surface, designed on the Lane A foundation.** This document specifies the UI that renders the orchestration platform's state: it cites the functional contracts it renders (Master Doc v2 tasks P2-10/11/13/14/29/39/41/44/45; OPS §3.4 event types and §4 state machines) and the design system it conforms to ([DesignSystem.md](DesignSystem.md) gates G1–G5, E1–E4, A1–A6, D1–D7; [DESIGN.md](../../DESIGN.md); the [Voice Bible](../creative/Mainguard_Voice_And_Delight_Bible.md)). It renders **states, not security** — the enforcement lives daemon-side; this surface's job is to make daemon truth legible.

**Tense note.** The Voice Bible marks control-center rules **[Horizon]**. This document is the sanctioned design of that horizon (Lane E's explicit mandate), and the P3/P2 prototype built from it runs on mock services only. Bible rules T-4, N-3, N-4, V-6 [Horizon] are treated as *binding* here, since this is the surface they were written for.

**Binding constraint, restated once:** this is an **extension of the Quiet Gatehouse system, never a new aesthetic**. Zero new tokens are minted in this document (§10 proves it). Every color is a `{DynamicResource}` semantic role; shape/spacing/type stay on the fixed scales; each view carries at most one signature accent; the whole surface reads in all five themes. Anti-references apply doubly: no Electron/VS-Code-extension chrome, no enterprise-SaaS card grids, no hero-metric scaffolding.

---

## 0. Revision of record — 2026-07-11 (supersedes §1 where they disagree)

Product feedback after the first prototype revised the shell decision. The binding shape is now:

1. **Integrated, not separate.** The control center is not its own window — it lives **inside MainWindow**, which keeps its full v1 chrome (File menu, branch pill, git dropdowns, Pull/Push). A new **section rail** (leftmost column, expandable/collapsible exactly like the repo sidebar; collapsed = icons only, every icon tooltipped) navigates: **top third** — Repo viewer (today's git workspace, untouched), Coordinator (attention-badged), Resources (opens the resource-monitor window), and the relocated git host icons (PRs, Issues, Notifications, Releases — moved off the top dropdown; single home); **bottom two-thirds** — the live agent list (collapsed tooltip = name — state · current task), with the kill switch at the rail's foot (always visible in every section).
2. **Two layouts, not three.** The Loom preset is retired. Flight Deck (default) and Conversation Deck are picked in **File → Layout**, persisted exactly like Theme (`UserPreferences.WorkspaceLayout`), and apply **to the coordinator surfaces only** — the Repo viewer never changes shape.
3. **Vibe is a separate app.** The Build/Pro in-shell toggle is dropped; VibeModeDesign.md now specifies the future standalone Vibe product's surface. Its views stay implemented and harness-rendered, reachable from no menu.

§1's preset mechanics, §2's activity-bar row specs, and the standalone-window framing are historical where they conflict with this section; everything else in the document (queue rail §3, workspace §4, coordinator/plan/kill-switch §5, cockpit §6, board §7, telemetry §8, badge family §9, conformance §10) binds unchanged.

## 1. The shell — three workspace presets, one system *(superseded by §0 where they disagree)*

### 1.1 The decision

The Control Center's fixed contract (P2-13) is: an **activity bar** (resource monitor + pinned tabs + LIFO agent list), a **Dock.Avalonia workspace** (per-agent terminal + diff + staging, layout persisted), and an **always-visible kill switch**. Within that contract there are three legitimate rooms, and the product decision (confirmed 2026-07-11) is to ship all three as **workspace presets — user-selectable exactly like themes**:

| Preset | What is permanently on screen | Who it serves |
|---|---|---|
| **Flight Deck** *(default)* | The merge-queue rail, pinned right. Coordinator docks bottom-left. | The reviewer-operator: queue state and `CanMerge` gating never leave the eye line. The verification pipeline is the product spine (Master Doc §1.2), so the spine is the default furniture. |
| **The Loom** | The queue as a horizontal weave strip across the top; full-width workspace below. | The watcher: the most on-metaphor reading — agent branches as threads converging into main; the stale cascade reads as the weave re-tensioning. Costs ~90 px of height. |
| **Conversation Deck** | The Coordinator conversation as a permanent left column; telemetry stack pinned right. | The delegator: runs the swarm by talking to it; plans and approvals live where the conversation is. |

Preset mechanics mirror theming exactly (N-5 by construction):

- The picker sits beside the theme picker (`View → Workspace`), radio-select, instant apply — a preset swap is a **Still** repaint (M-4 extended: layout state, not spectacle; no transition choreography between presets).
- Dock.Avalonia persists layout **per preset**: hand-adjustments inside Flight Deck don't bleed into The Loom. `WorkspacePreset` is view-state serialized beside the dock layout — never a data migration.
- All three presets are compositions of the **same components** specified in §2–§8. A preset chooses *where* a panel docks and whether it is pinned; it never restyles a panel. This is the same discipline as themes: five palettes, one system — three rooms, one furniture set.
- Preset names follow N-1's register (a place of work / a quality of the instrument, never a literal description).

Everything below is specified against **Flight Deck** as the reference geometry; §1.4 states what The Loom and Conversation Deck change.

### 1.2 Flight Deck — the reference frame

```
┌───────────────────────────────────────────────────────────────────────────┐
│ ⌂ mainguard ▾   ⑂ main        Mainguard            ⛔ Stop all     ─  □  ✕   │  title bar (§5.4 kill switch)
├────────┬─────────────────────────────────────────────────┬────────────────┤
│ ACTIVITY│  WORKSPACE (Dock.Avalonia)                     │ MERGE QUEUE    │
│ BAR §2 │  §4 — per-agent documents + tool panels        │ RAIL §3        │
│        │                                                 │                │
│ CPU ▂▄▅│ ┌ Loom-3 ─ terminal ──────┬─ diff ───────────┐ │ main ────────● │
│ RAM ▃▃▄│ │ $ …                     │ +42 −7 · 3 files │ │ ├─◉ Loom-3     │
│ ¢ $2.14│ │ ▌                       │ src/Auth.cs      │ │ │  Verified    │
│ ────── │ ├ staging ────────────────┴──────────────────┤ │ │  [Review]    │
│ ◎ Coord│ │ 3 staged                                   │ │ ├─◐ Loom-1     │
│ ● Loom3│ └────────────────────────────────────────────┘ │ │  Verifying   │
│ ◐ Loom1│ ┌ Coordinator ────────────────────────── ⌃ ──┐ │ ├─● Loom-4     │
│ ● Loom4│ │ TaskPlan #7 · 4 files   [Approve] [Reject] │ │ │  Working     │
│ ◌ Loom2│ └────────────────────────────────────────────┘ │ └─◌ Loom-2     │
│        │                                                 │    Stale ↻     │
└────────┴─────────────────────────────────────────────────┴────────────────┘
```

Surface stepping is the shipped vocabulary: window = `SurfaceWindow`, the three regions are `SurfacePanel` cards (radius 12, 1 px `BorderHairline`) separated by the transparent 8 px `GridSplitter` gutters — floating panels, never border-fused grid cells (DESIGN.md §5). Terminals and diff editors sit on `SurfaceDeep` inside their cards, exactly like the shipped diff viewer.

### 1.3 The accent budget (the One Accent Rule, per region)

Each dockable panel is a view for the purposes of DESIGN.md's One Accent Rule. The accent always marks *the one thing the human should do next*:

| Region | Its one accent | Everything else |
|---|---|---|
| Activity bar | the Coordinator tab's attention dot (§2.4) | badges are semantic brushes; sparklines are `TextMuted` |
| Merge-queue rail | the `[Review]` CTA on the front-most *fresh* `Verified` entry (§3.4) | state chips are semantic; main's thread is `Lane1` |
| Workspace (per agent) | the composer's focused send action (§4.2) | terminal/diff/staging are readouts |
| Coordinator panel | `[Approve]` on the oldest pending plan card (§5.2) | Reject is `Button.Secondary` |
| Review cockpit | the Merge button, only once `CanMerge` is true (§6.4) | acknowledgments are checkboxes, not accents |
| Session board | `[Pick winner]` inside an open comparison (§7.3) | cards are neutral |
| Telemetry panels | **none** — readouts earn silence (§8) | — |

### 1.4 What the other presets change (and only this)

- **The Loom**: the queue rail's content re-renders as the horizontal weave strip (§3.6) docked top, full-width; the rail slot is freed. Coordinator docks as a workspace document tab. Everything else identical.
- **Conversation Deck**: the Coordinator panel (§5) pins as a fixed left column between activity bar and workspace; the queue renders in its compact form (§3.7) at the top of the telemetry stack, which pins right (§8 panels stacked). The workspace center swaps between cockpit / board / agent documents.

---

## 2. The Activity Bar (P2-13)

The narrow (56 px) always-visible left rail: the swarm's pulse at a glance. Two rows per the binding contract.

### 2.1 IA

```
┌────────┐
│ CPU ▂▄▅ │  Row 0a — resource monitor: two sparklines (VM CPU, RAM)
│ RAM ▃▃▄ │            + the token-spend counter beneath ($ from GatewayService)
│ ¢ $2.14 │
│ ────────│  hairline divider
│ ◎ Coord │  Row 0b — pinned tabs: Coordinator (with attention slot),
│ ⊞ Board │            Session board, Review cockpit, Telemetry
│ ────────│
│ ● Loom-3│  Row 1 — virtualized LIFO agent list (newest first),
│ ◐ Loom-1│            status micro-badge + name; selected row gets the
│ ● Loom-4│            3 px AccentBrush rail (shipped selection vocabulary)
│ ◌ Loom-2│
└────────┘
```

### 2.2 Resource monitor

Sparklines are ambient readouts, not subjects: 1 px `TextMuted` strokes on transparent, no fill, no axis, 30-sample rolling window redrawn **Still** (readouts never tween, DesignSystem §4.1). The token-spend counter is `TextBlock.Mono` Label-scale `TextMuted`; it ticks Still. Hover tooltip (TT-1): `Sandbox VM: 34% CPU · 2.1 GB — spend today $2.14 across 4 agents`. When spend crosses a configured budget threshold the *counter's brush* steps to `WarningBrush` (one Shift-130) and the figure stays a figure — no badge, no pulse; `budget_exceeded` (OPS §3.4) is what escalates, as a chat card (§5.3) and OS notification.

### 2.3 The agent list (Row 1)

Virtualized, LIFO. Row anatomy (28 px): `[micro-badge 10×10, reserved slot] [name, Body] [right-aligned Label detail]`. The detail slot carries the one live fact per state — `41/58` while Verifying, `12m` idle for PlanPending, `paused` — always text beside the badge (E4: a dot never carries state alone). Names are N-4 working names (`Loom-1…n`) in `TextBlock.Mono`. Selecting a row focuses/opens that agent's workspace document. Rows for `ReviewHibernated` agents rest at the 0.60 stop (present, not the subject); `TornDown` rows leave the list (Release-130).

### 2.4 The Coordinator tab and the attention signal

The P2-13 contract names an `IsAttentionRequired` **pulse**. A looping pulse fails the motion grammar outright (D3: no primitive repeats; M-2: restraint), so the pulse is redesigned as a **static attention badge that arrives once**: when `attention_required` fires (OPS §3.4 — PlanPending reminders, AwaitingReview idle), a solid `WarningBrush` dot with a count (`◎ 2`) settles into the tab's reserved badge slot (Settle-140, E3 — the slot is always there, nothing reflows) and **stays at full strength, Still**, until the underlying items are decided. The interruption channel is the OS notification the daemon already emits (suppressed when that agent is foregrounded, per contract); the badge is the persistent truth. Meaning survives with zero motion (M-7) — a pulse would decay into wallpaper by the tenth firing; a count is information. *This is a deliberate refinement of P2-13's word "pulse": the contract's intent (attention is unmissable) is met by position + badge + OS notification.*

### 2.5 States

- **Daemon `Connected`**: normal. **`Degraded`**: a full-width 2-line banner under the title bar, Still at full strength (D5 — a degraded control plane is a hazard class): `Reconnecting to the daemon — state may be stale. Agents keep running.` (V-6: says what is and isn't affected). **`Down`**: the bar's dynamic rows empty into the ES-3 empty state, §9.1.
- **Empty (zero agents)**: Row 1 shows one `TextMuted` Body line, `No agents running.` — the full empty state lives in the workspace (§9.2), not the 56 px rail.
- **Loading**: rows appear composed from the first `ListAgents` snapshot; no skeleton in a 56 px rail (it would read as noise at this size).

### 2.6 Five themes

The bar is `SurfacePanel` + hairline like every sidebar; badges are semantic brushes gated by A4 (≥3:1 on panel + selection composite in all five, per DesignSystem §3.11); the attention dot is `WarningBrush`, which survives Daylight's ink retune at 6.08 as text and ≥3:1 as a mark. Nothing here is theme-conditional.

---

## 3. The Merge Queue Rail (P2-10) — the spine made visible

The queue is the product's moat and the rail is its instrument face: **every agent branch is a thread; main is the warp**. The rail renders the §4.2 OPS state machine *exactly* — its states, its stale cascade, its gate — and nothing the machine can't do (no drag-to-reorder, no illegal affordances: projection, not invention).

### 3.1 IA

```
┌────────────────┐
│ MERGE QUEUE    │   header (Title 16)
│ main ────────● │   the warp thread: Lane1, 2 px round cap,
│ │              │   terminating in the current main dot
│ ├─◉ Loom-3     │   entry: thread joins main; micro-badge + name
│ │  Verified    │     state word (N-3, Label) + freshness fact
│ │  main@d4e1f  │     the record's MainSha in mono (V-6: the claim
│ │  [Review]    │     is auditable) + the rail's ONE accent CTA
│ ├─◐ Loom-1     │
│ │  Verifying   │
│ │  tests 41/58 │   live counter, ticks Still
│ ├─● Loom-4     │
│ │  Working     │
│ └─◌ Loom-2     │
│    Stale       │   StaleVerified: state word + the re-queue fact
│    ↻ rebasing… │
│ ────────────── │
│ 1 flagged item │   the CanMerge gate line (§3.4)
│ unacknowledged │
└────────────────┘
```

Threads draw with the shipped graph vocabulary — 2 px, `PenLineCap.Round`, `Lane1` for main, entry connectors in `BorderHairline` (they are structure, not data; lanes stay reserved for the *commit graph's* topology so the two instruments never visually compete). Entries are ordered by state proximity to merge (Verified-fresh first), then queue age.

### 3.2 State encoding (the queue chip set)

Queue state is a **word first** (E4 — `Verified`, `Verifying`, `Working`, `Stale`, `Rejected`; N-3: names that read the same in an audit log) with a micro-badge (§9.3's family) and a semantic brush by meaning:

| `WorkerMergeState` | Badge form | Brush | The fact line beneath |
|---|---|---|---|
| `Working` | solid disc | `TextPrimary` | last activity, relative time |
| `Verifying` | half disc (the Part-2 `PendingIcon` construction) | `InfoBrush` | live test counter (Still) |
| `Verified` | ring + check | `SuccessBrush` | `main@<sha7>` it verified against |
| `StaleVerified` | ring with a broken arc (the fractured-contour cue, §9.3) | `WarningBrush` | `↻ re-verifying against d4e1f…` |
| `AwaitingReview` | ring + center dot ("waiting on you") | `SuccessBrush` | `sitting 22 min` |
| `Merged` | check, no ring | `SuccessBrush` | leaves the rail after its Exchange (§3.5) |
| `Rejected` | ✕ (`DismissIcon`) | `DangerBrush` | `branch kept until teardown` (V-5) |

Zero-color survival (E1): every state differs by silhouette or fill mode *and* carries its word. Brush is the third channel, never the first.

### 3.3 The stale cascade — the loom re-tensions

When a merge lands, `NotifyMainMoved` flips every `Verified` entry to `StaleVerified` and re-queues it. On the rail this is the **signature delight of the whole surface**: main's dot advances (Still — a readout), then each affected entry's chip **Exchanges** (130 ms) from `Verified` to `Stale ↻` *as its `queue_state` event arrives* — a visible ripple of truth running down the rail. Each Exchange is its own state transition with its own trigger, so the sequence is D2-legal (a sequence of states, not choreography). The user *watches the reason* their other branches must re-verify. Comprehension arriving, 130 ms at a time — nothing else in the market shows this at all.

### 3.4 The gate, always honest

The rail's footer is the `CanMerge` truth for the selected entry: when false, the *reason* renders as a plain line (`FAILED_PRECONDITION` taxonomy → human words, V-1): `1 flagged item unacknowledged` · `verification is stale — re-verifying` · `no test command configured`. The `[Review]` accent CTA opens the cockpit (§6). The **stale-override path is never on the rail** — it lives inside the cockpit behind the explicit, loudly-labeled setting (P2-10 invariant: every non-fresh path warns and records), because a rail is a glance surface and overrides are a decision surface.

### 3.5 Motion ledger (D1 addendum — the rail's earned moments)

| Moment | Verdict | Primitive |
|---|---|---|
| A merge lands (the thread rejoins) | Earns — §4.4.1's pill is the celebration; the rail repaints composed | Still (rail) + the toast's Settle/hold/Release |
| The stale cascade | Earns — the signature comprehension moment | per-entry Exchange-130 as events arrive (§3.3) |
| An entry turns `Verified` | Earns quietly | Exchange-130 on the chip; OS notification if unfocused (T-4 wording) |
| A new thread joins the rail (spawn) | Housekeeping | Settle-140 of the new row; the thread paints composed |
| Counters (`41/58`, sitting time) | Readout | Still, always |

### 3.6 The Loom preset rendering

Same data, rotated: main runs left→right as the top thread (`Lane1`); entries are threads below, joining main at merge. State chips sit at each thread's head; the cascade ripples left-to-right. The strip is fixed-height (88 px = 4 rows + header), virtualizing beyond 4 agents with a `+3 more` overflow row — a strip must never scroll the shell (the workspace below owns the scroll).

### 3.7 The compact rendering (Conversation Deck / telemetry stack)

A 3-line summary card: `Queue — 1 ready · 1 verifying · 1 stale` + the gate line + the one `[Review]` accent. Clicking any line opens the full rail as a workspace document.

### 3.8 Empty / loading / error

- **Empty**: `Nothing queued.` one `TextMuted` line (ES-1 register; no button — the queue fills by agents working, not by a CTA).
- **Loading**: composed from the first snapshot; the rail never skeletons (it is small and late-bound).
- **Error** (daemon `Degraded`): the rail dims to the 0.60 stop and its header gains `— stale` (V-6: never render possibly-stale state as fresh).

---

## 4. The Workspace (P2-13 dock + P2-39 pack + P2-44 strip)

The center: one Dock.Avalonia document per agent plus dockable tool panels (Coordinator §5, cockpit §6, board §7, telemetry §8). Layout persists per preset. Teardown discipline is binding (P2-13): closing an agent document disposes its terminal control, stops timers, closes floating windows.

### 4.1 The agent document

```
┌ Loom-3 · fix/auth-refresh ── ● Working ────────────────────────────┐
│ ┌ health strip (§4.4) ─ egress 0 · procs ok · net ▂▁▂ ──────── ⌄ ┐ │
│ ├ terminal ──────────────────────────┬ plan tree (P2-39.4) ──────┤ │
│ │ $ …                                │ ▸ Refactor token refresh  │ │
│ │ ▌                                  │   ✓ Read auth module      │ │
│ │                                    │   ▸ Write failing test    │ │
│ ├ diff ──────────────────────────────┴───────────────────────────┤ │
│ │ +42 −7 · 3 files · provenance: Loom-3 · task #7               │ │
│ ├ composer ──────────────────────────────────────────────────────┤ │
│ │ › follow-up prompt…                              [Send]        │ │
│ │ queued (2): "also update the docs" ✕ · "run the lint pass" ✕  │ │
│ └──────────────────────────────────────────────────────────────────┘
```

- **Tab chrome**: micro-badge + working name + branch in mono. A managed worker whose terminal is daemon-locked renders the input line with a `LockIcon` + `Read-only — managed by the Coordinator. Steering goes through prompts.` (TT-2; the lock is daemon-enforced — the UI states it, honestly, V-6).
- **Terminal**: the vendored renderer on `SurfaceDeep`. A readout: it never animates beyond its own byte stream.
- **Plan tree** (P2-39.4): read-only, parsed from adapter events; nodes are text + the check/pending glyph vocabulary (audit row 7); updates tick Still.
- **Diff + staging**: the shipped T-13/T-06 surfaces, reused as-is, with the provenance line (§6.2's chip vocabulary) above.

### 4.2 The composer and the message queue (P2-39.1)

The composer is the document's accent carrier (focused border = `AccentBrush`, the shipped input focus rule). While the adapter streams, sends **queue** instead of interrupting: queued messages render as chips beneath the input (`Border.RefChip` chrome, radius 999, `AccentSelection` fill) each with its ✕ cancel — visible, reorderable by drag, cancellable, exactly the daemon FIFO (P2-39 invariant: survives restart). The state is worded in the input's placeholder: `Loom-3 is streaming — messages queue until it's idle.` (TT-2 doubling as the fix).

### 4.3 Prompt-first dispatch (P2-39.2)

The shipped command palette (T-18 chrome, reused verbatim — one overlay vocabulary in the whole app) gains `New session:` — type the prompt, then inline pick repo → agent CLI → base branch as three chip pickers in the palette's footer, Enter dispatches. Managed mode routes it through plan approval (§5.2) — the palette confirms with `Plan requested — the Coordinator will draft it for your approval.` toast (T-1). Manual mode spawns directly (still admission/budget-gated; a `RESOURCE_EXHAUSTED` refusal renders pattern-E: `Agent limit reached — 4 of 4 running. Stop one, or raise the limit in Settings → Agents.`).

### 4.4 The health strip (P2-44, ambient form)

One Label-height line at the document's top, always present (E3): `egress 0 · procs ok · net ▂▁▂` — three facts, `TextMuted` when clean. A blocked egress attempt flips the fact to `egress 1 blocked` in `WarningBrush` (Shift-130) and stays until viewed. Clicking the strip opens the full telemetry panel (§8.1) scoped to this agent. The strip is a fact line, not an alarm: verifiable trust as daily furniture (P2-44's thesis), worded so it would read identically in an audit log (V-6).

### 4.5 Empty / loading / error

- **No document open**: the workspace shows the zero-agents empty state (§9.2) or, with agents running, `Select an agent to open its workspace.` one-liner.
- **Terminal reattach after daemon restart**: the terminal renders the replay buffer composed; a one-line `Reattached — session continued.` notice (Still) above the prompt.
- **Agent `Dead`**: the document's content is replaced by pattern-E: `Loom-4's container died. The worktree is kept for inspection; its branch is intact.` + `Button.Secondary` "Inspect worktree" / "Tear down" (`Button.Danger`) — V-5's way back before the cleanup verb.

---

## 5. The Coordinator & Plan Approval (P2-14)

### 5.1 The conversation surface

A chat panel in the app's own register — **not** a consumer chat skin: messages are left-aligned blocks on `SurfacePanel`, sender label (Label, `TextMuted`: `Coordinator` / `You`) above Body text, no bubbles, no avatars (V-3: an instrument, not a companion). Tool calls the Coordinator makes render as one-line mono facts (`spawn_worker(fix/auth-refresh)` · audit-mirrored), collapsed by default under a `⌄ 3 tool calls` expander — the conversation stays a conversation; the audit trail stays reachable (V-6).

### 5.2 The TaskPlan approval card — the product thesis, as a card

The two-phase spawn's approval moment gets the surface's most deliberate design. The card renders the schema's three fields as three labeled sections — **never prose-blob approval**:

```
┌ TaskPlan #7 — Refactor token refresh ────────────────┐
│ Scope        src/Auth/*.cs · tests/AuthTests.cs (4)  │  every path, mono,
│ Approach     Extract ITokenClock; inject in refresh  │  expandable list
│ Test         AuthTests green + new expiry cases      │
│ ──────────────────────────────────────────────────── │
│ Budget $1.50 · admission 3/4 · drafted 2 min ago     │  the facts row
│                            [Reject]  [Approve plan]  │
└───────────────────────────────────────────────────────┘
```

- `[Approve plan]` is the panel's one `Button.Accent` (approval is the thesis, and it is *not* destructive — Danger is reserved for loss). `[Reject]` is `Button.Secondary`. The card states the plan's budget and the admission headroom *before* the decision (V-4's shape applied to a non-destructive gate: what will change, what it costs).
- **Scope is the load-bearing field**: it is what the cockpit's out-of-scope flag (P2-11 step 5) later enforces, so it renders as the card's largest section, every path visible. Approving a plan approves *a scope*.
- Decided cards collapse to one history line (`Plan #7 approved — Loom-3 spawned`, with the daemon-derived approver identity in the tooltip, TT-3/V-6 — the UI never displays a client-editable identity because none exists in the protocol).
- **Approval-fatigue pressure** (P2-14 S-8): when >2 plans sit pending, the panel header gains a Still fact line — `3 plans pending — the oldest has waited 12 min.` The design defends the human gate by making queue pressure visible, never by softening the gate.

### 5.3 Orchestrator events as chat facts

OPS §3.4 events that concern the whole swarm render as **system lines** (Label, `TextMuted`, centered dot separator), not cards: `· Loom-1 verified against d4e1f — 58 tests green ·`. Only events needing a decision (plan_pending, escalations) become cards. The conversation is therefore scannable: cards = decisions, lines = history, both Still on arrival except a card's single Settle-140.

### 5.4 The kill switch — the always-visible brake

One control, title bar, right of center, fixed position in **every** preset and **both modes** (it survives into Vibe Mode as "Pause everything", VibeModeDesign §3).

- **At rest**: quiet — octagon glyph (`SeverityBlockerIcon`'s silhouette family: the stop shape, E2) + `Stop all`, ghost chrome (`SurfaceHoverGhost` rest, hairline border, `TextMuted`). A permanently red button would be alarm-as-decoration (V-2) and heavy color on an inactive state (the product-register ban); the brake is findable by *fixed position + the octagon silhouette*, not by shouting.
- **Hover**: Shift-130 to `DangerBrush` fill + `OnAccent` text — the consequence becomes legible exactly when the hand approaches.
- **Pressed**: fires immediately. **No confirmation dialog** — the deliberate exception to C-pattern gating, because the action is (a) an emergency stop whose cost-of-delay is the hazard itself, and (b) *recoverable by design* (freeze + pause, nothing destroyed — the safer path IS the button, V-4). The click is the confirmation.
- **Engaged**: the button Exchanges to solid `DangerBrush` `Frozen — resume`, and a full-width banner renders Still at full strength (D5: the brake is born complete, 0 ms): `All agents paused. The merge queue is frozen. Nothing was lost — resume when ready.` (V-5 in the same breath). The OPS §4.5 phases (`QueueFrozen → PerAgentYield → Frozen → Snapshotted`) tick through the banner's fact line as Still text — the machine's honesty, rendered: `queue frozen · 3 of 4 agents paused · snapshotting…`.
- **Resume** is the banner's one action (`Button.Primary` — deliberate: resuming is routine, not celebratory).

### 5.5 Empty / loading / error

- **Empty conversation**: Hero-less (the panel is small): `The Coordinator plans and delegates — it never touches code or merges.` one `TextMuted` line + the composer (an empty state that teaches the trust model, ES-1 + V-6).
- **Coordinator `RESOURCE_EXHAUSTED` on drafting**: pattern-E line in-conversation: `Plan limit reached — 5 drafts are already waiting on you. Decide those first.`
- **Loading**: transcript renders composed from persistence.

---

## 6. The Review Cockpit (P2-11) — where trust is manufactured

The daily-driver surface: risk-ranked review of one agent branch, gated by acknowledgment. Opens as a workspace document from the rail's `[Review]`, the board, or an OS notification.

### 6.1 IA

```
┌ Review — Loom-3 · fix/auth-refresh → main ──────────────────────────┐
│ verified @ d4e1f · fresh · 58 tests green (+2 new)   [test delta ⌄] │  header facts
│ ┌ FLAGGED — acknowledge to enable merge ──────────────────────────┐ │
│ │ ⬡ package.json — scripts block edited        [view] [✓ ack]    │ │  the gate panel
│ │ ⬡ outside approved scope: docs/notes.md      [view] [✓ ack]    │ │  (§6.3)
│ └──────────────────────────────────────────────────────────────────┘ │
│ ┌ hunks, ranked ──────────────────┬ diff (T-13, reused) ──────────┐ │
│ │ 1 ⬡ package.json  ExecutableCfg │                               │ │
│ │ 2 ▲ src/Auth.cs   Source        │   … the shipped diff view …   │ │
│ │ 3 ○ docs/notes.md Docs          │   provenance chip per hunk:   │ │
│ │                                 │   ⑂ Loom-3 · task#7 · a1b2c3d │ │
│ └─────────────────────────────────┴───────────────────────────────┘ │
│ 12 of 14 hunks viewed · 2 acks remaining      [Bring local] [Merge] │  footer gate
└──────────────────────────────────────────────────────────────────────┘
```

### 6.2 Rank and provenance

- Files/hunks order by `RiskClassifier` rank — the list is *the review plan*. Rank category renders as the §9.3 severity vocabulary (octagon = must-acknowledge classes, triangle = risky, circle = informational) + the category word (E4). Ordering never hides (P2-11 invariant 3): everything reachable, nothing collapsed by rank.
- The provenance chip per hunk is `Border.RefChip` chrome: `⑂ Loom-3 · task #7 · a1b2c3d` (Agent-Trace-sourced; trailer-sourced chips add `· from trailers` in the tooltip, TT-3 — the source of a provenance claim is itself provenance). A human commit simply has no chip (absence is honest, V-6; no crash, no placeholder).

### 6.3 The flagged gate — acknowledgment as a first-class act

The flagged panel is pinned above the hunk list (never a modal — the review continues around it). Each item: severity glyph + the *fact* (`scripts block edited`, `outside approved scope — the plan covered 4 files, this touches docs/notes.md`) + `[view]` (jumps the diff) + its own `[✓ ack]` checkbox. **Item-by-item, never a global checkbox** (P2-11 rejection trigger). Acks bind to the diff hash: a new push resets them and the panel states why — `The branch changed since you acknowledged — 2 items reset.` (V-6). The panel's count mirrors into the rail's gate line (§3.4) and the footer.

### 6.4 The merge gate and the footer

The footer is the `CanMerge` equation rendered as facts: hunks-viewed count (P2-38 coverage), acks remaining, freshness. `[Merge]` is the document's one `Button.Accent`, **enabled only when the daemon says `CanMerge`** — its disabled tooltip is the current reason verbatim from §3.4's vocabulary (TT-2). Merging fires the shipped journaled foreground merge; success is §4.4.1's pill (`Merged fix/auth-refresh into main.`) and the rail's cascade does the rest. The **stale override** lives behind the settings flag; when enabled it renders as a `Button.Danger` labeled `Merge stale (override)` beside the disabled accent — loud by role, journaled + audited by contract, never silent (S-4).

`[Bring local]` (the Sculptor-parity hand-back) is `Button.Secondary`: fetches the branch into a local worktree via T-29 — the tooltip states exactly what it does (`Fetches agent/loom-3 into a new worktree — the agent keeps working`).

### 6.5 The test-delta strip

One row under the header: `58 green (+2 new) · 0 failed · command unchanged from main` — the last clause is RT-D2's provenance made visible; when the test command *did* change, that clause becomes a flagged item in §6.3 (already in the detector's contract) and the strip words it plainly: `test command changed on this branch — flagged below`.

### 6.6 Motion, empty, themes

Earned: the moment the last ack lands and `[Merge]` enables — the accent lights via the global Shift-130 (the §4.4.2 "way forward lights up" vocabulary, reused exactly). Everything else is a readout. Empty (no reviewable branches): `Nothing awaiting review.` + `Button.Secondary` "Open session board". The cockpit reads in all five themes by construction — every element is shipped vocabulary (diff tints per A2/A3, chips, severity glyphs per A4).

---

## 7. The Session Board (P2-29)

### 7.1 The board — a projection, not a tracker

Columns are the P2-10 states verbatim (`Working · Verifying · Verified · AwaitingReview · Done`), cards are agents/tasks. **The anti-reference pressure is highest here** — this must not become an enterprise kanban: cards are single-density rows-in-columns (name, branch mono, one fact line, micro-badge), no cover images, no assignee avatars, no WIP-limit chrome. Drag exists **only where a real transition exists** (AwaitingReview → Working with a follow-up prompt — the drop opens the composer pre-focused); every other drag target refuses inertly (the card returns Still; no bounce, D3). `Merged` and `Rejected` collapse into a `Done` column at the 0.60 stop, newest first.

### 7.2 Badges

`Conflict` and `RateLimited` ride as `Border.RefChip` chips on the card (word + glyph, E4) — they are facts about the entry, not columns (they overlay any state).

### 7.3 Comparison — "which branch is green and cheapest"

Select 2–3 cards → `[Compare]` (`Button.Secondary`) opens the comparison document: N columns, one per candidate, each column = verification record (state word + `main@sha` + test counts) · spend (mono) · diff-vs-main summary · risk profile (counts per category, ranked) · provenance summary, above a synced side-by-side T-13 diff. The verdict row pins to the top — verification + cost first, diffs second (the P2-29 "beat" is the *order of information*). `[Pick winner]` is the view's one accent on the focused column; it confirms with a C-pattern dialog because it **rejects the others** (destructive to their branches): `Pick Loom-3 and archive 2 candidates? Their branches are kept until teardown; their worktrees are pruned.` — `Button.Danger` "Pick and archive", Cancel. (C-1/C-2: the recoverable and the destroyed, named.)

### 7.4 Empty / five themes

Empty board = the §9.2 zero-agents state. Theme reading: columns are `SurfacePanel` on `SurfaceWindow` with hairline separators — no per-column tinting (colored columns would make state color-first, violating E1's spirit at the layout scale).

---

## 8. Telemetry — health, the recorder, and the remote (P2-44/45/41)

Three panels, one register: **readouts that earn silence**. No accent anywhere in §8 (§1.3) — telemetry is the part of the instrument that must never perform.

### 8.1 Sandbox health & egress (P2-44)

The full panel behind §4.4's strip. IA: a per-agent section (selectable from the strip or the list), each a chronological fact table — `14:02 · egress blocked · pastebin.com · curl` / `14:07 · secret-file read attempt · .env` / `13:58 · quarantine push · agent/loom-3` — `TextBlock.Mono` timestamps, plain words, severity glyph per row (§9.3). Rows arrive Still (they are an audit projection; animation would dramatize, D5). A summary header counts by class. **Alerts are events, never auto-kills** (P2-44 invariant) — the panel's only affordance is `[Open audit entry]` per row and the standing kill switch in the title bar. The empty state is the quiet win: `No blocked egress, no secret access attempts, no anomalous processes — 4 sandboxes healthy.` (ES-4's license: a genuine all-clear earns one Settle-140.)

### 8.2 The flight recorder (P2-45)

Lives inside the cockpit and the agent document as a **scrub affordance, not a video player**: selecting a hunk in review offers `[⏵ Watch this hunk land]` (`Button.Secondary`, Label size) → opens the recorder document: the PTY replay on `SurfaceDeep` with a timeline rail beneath, tick-marked by commit (mono sha7 labels), the selected hunk's moment pre-centered. Transport is play/pause/scrub only — replay is read-only by invariant, and the UI states it once in the header: `Recording — replay only. Secrets were masked before storage.` (V-6, the G-13 fact). Scrubbing is Still (a readout of time); play advances the terminal at recorded pace. Retention gaps render as hatched timeline segments with `pruned by retention` tooltips — the recorder never pretends continuity (M-6's honesty applied to time).

### 8.3 The remote dashboard (P2-41)

A responsive web projection of four things only: the board (§7.1 compact), the attention list, plan approval cards (§5.2, full fidelity — approving from the phone is the feature), and the kill switch + spend line. Design constraints for the SPA: it uses the same tokens exported as CSS custom properties per theme (the five themes ship to the web surface; Midnight default), the same type ramp, the same state words — **the phone is the same instrument, smaller**, not a companion app aesthetic. Pairing UI (desktop side): a P2-41 QR/short-code card in Settings with the scoped-role choice worded plainly: `Approve & observe` / `Observe only` — and the paired-devices list with per-device `[Revoke]` (`Button.Danger`, C-pattern confirm). Remote approvals render in the desktop chat with their device identity (`approved from Pixel-8`, V-6/P2-15).

---

## 9. Shared vocabulary — states, empties, and the badge family

### 9.1 The daemon-down empty state (ES-3 — capability, not error)

Workspace-central: Hero `The control center needs its daemon` · Body `mainguardd runs agents, sandboxes, and the merge queue. Start it to bring this surface to life.` · `Button.Accent` "Start daemon" · plain link "What runs where?" (opens the Companion doc's topology — teaching, not troubleshooting). No `DangerBrush` anywhere (ES-3).

### 9.2 The zero-agents empty state

Hero `No agents running` · Body `Spawn a worker from a plan, or describe the task and let the Coordinator draft one.` · `Button.Accent` "New session" (opens §4.3's prompt-first palette) · secondary link "Open a plain repo instead" (the v1 client is always one step away — the two products stay one app).

### 9.3 The agent micro-badge family (the third icon family)

Per E2, agent lifecycle is **one silhouette family — the ring** (the bobbin: a thread's holder), 10×10, joining the shield (trust) and the severity primitives (§2.4) as the app's third state family. Fill mode + inner mark carry the state; the brush is semantic by meaning; **a badge never appears without its state word somewhere at its surface** (E4 — list rows carry the detail slot, chips carry words, tabs carry tooltips):

| State(s) | Form (zero-color reading) | Brush |
|---|---|---|
| `Working` | solid disc — full activity | `TextPrimary` |
| `Provisioning` | ring, bottom-half arc only — assembling | `TextMuted` |
| `Verifying` | half disc in ring (Part 2's `PendingIcon` construction) | `InfoBrush` |
| `PlanPending` / `AwaitingReview` | ring + center dot — waiting on a human | `WarningBrush` / `SuccessBrush` |
| `Paused` / `ReviewHibernated` | two vertical bars in ring (pause plates) | `TextMuted` |
| `RateLimited` | ring + horizontal bar — throttled | `WarningBrush` |
| `Unresponsive` | dashed ring — contact broken (the fractured-contour cue, §2.3's vocabulary) | `WarningBrush` |
| `StaleVerified` (queue axis) | ring with one broken arc + check | `WarningBrush` |
| `Verified` | ring + check | `SuccessBrush` |
| `Merged` | check alone — the ring has served | `SuccessBrush` |
| `Rejected` / `Dead` | ✕ (`DismissIcon`) | `DangerBrush` |

Grayscale self-test (E1): solid / halved / dotted / barred / dashed / broken-arc / check / ✕ — eight distinguishable forms at 10 px, verified in the Part 3 render-harness grayscale capture before shipping. The `AgentStatus → Brush` converter (P2-13's contract) maps exactly this table; the *shape* mapping rides the same converter pattern (`AgentStatus → Geometry`).

### 9.4 New-string inventory note

Every user-facing string in this document passes the Bible's five-question gate by construction (objects named, ways back present, audit-legible, no filler, severity on the role); final string arbitration belongs to Microcopy.md, which should absorb §5.4's banner, §9.1/§9.2's empties, and the §3.4 gate-reason vocabulary as its [Horizon] section when the prototype lands.

---

## 10. Conformance record (the Part 1 self-gate)

- **Zero new tokens.** Every color reference above resolves to the existing 32-token contract + `GlyphPlate` (Part 3) + `Lane1–5` (Part 1). New *geometries* (the ring family, §9.3) are theme-independent icons in `App.axaml`, exactly like Part 2's triads. New *component classes*: none — `Button.*`, `Border.RefChip`, `Border.Card`, `Border.toast`, `TextBlock.Mono`, the palette chrome, and the selection rail cover every element drawn.
- **One accent per view**: §1.3's budget table names it for all eight regions; telemetry deliberately carries none.
- **Five themes**: no surface above is theme-conditional; every pairing used is gated by A1–A6 (badges A4, text A1, diff tints A2/A3); the two known accent-adjacency risks (Command Deck accent≈Success, Atelier accent≈Warning, documented in SurfaceDesigns) are mitigated here the same way — state words and silhouettes carry meaning before hue does.
- **Motion**: every timed motion above appears in §3.5's D1 ledger addendum or reuses a DesignSystem §4.4 storyboard; the kill switch and all hazard/telemetry surfaces are Still by D5; the attention "pulse" is redesigned to a static badge with a stated rationale (§2.4).
- **State machines**: the rail (§3), board (§7.1), and badge family (§9.3) render only OPS §4.1/§4.2 states and offer only legal transitions; no auto-anything is affordanced (no auto-merge, no auto-approve, no auto-kill).
- **Anti-references held**: no card grids (the board is rows-in-columns at one density; the comparison is columns of facts), no hero metrics (spend is a Label-size mono line), no consumer chat skin (§5.1), no web-view chrome.
