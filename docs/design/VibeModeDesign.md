# GitLoom Vibe Mode — Design Specification (Lane E Part 2)

**Status: DESIGN SPEC — the zero-knowledge surface for non-technical founders.** *(Revision 2026-07-11: Vibe ships as its own app, not an in-shell mode — the P3-03 "mode toggle" sections describe the surface itself; the toggle mechanics transfer to the standalone app's relationship with GitLoom desktop.)* Companion to [ControlCenterDesign.md](ControlCenterDesign.md); same conformance base ([DesignSystem.md](DesignSystem.md) G/E/A/D gates, [DESIGN.md](../../DESIGN.md), the [Voice Bible](../creative/GitLoom_Voice_And_Delight_Bible.md)). Functional contracts rendered: P3-02 (escalation triage), P3-03 (mode toggle, chat, live preview), P3-04 (one-click deploy), P3-01 (checkpoints — the safety substrate the copy leans on). Chrome decision (confirmed 2026-07-11): **Soft Collapse** — Vibe keeps the title bar and the renamed safety control; it is honestly the same app, simplified, never a fork.

**The emotional brief.** The Vibe user is building something real with no mental model of git, tests, or sandboxes. The surface must feel **warm, jargon-free, and calm** — and the design position is that in this system, *warmth is a property of honesty, not decoration*: the founder relaxes because the surface keeps proving that nothing can be lost (P3-01's checkpoints) and that someone competent is narrating what's happening. No mascots, no confetti, no "oops" (V-3 still binds); the warmth budget is spent on **plain words, generous space, and visible safety**.

**The Vibe dialect** (a sanctioned register extension, not a new voice): every Bible principle holds (V-1 precision, V-2 calm, V-5 the way back, V-6 honesty, V-7 economy), with two dialect rules on top —
- **D-V1 — Plain words for plain people.** No git/infra nouns in primary copy: never "commit/branch/merge/rebase/sandbox/daemon" — say "saved your progress", "a safe copy", "checking it works". The technical noun lives one level down, inside "Show technical details" (TT-4 inverted: the *jargon* is the supplementary layer here).
- **D-V2 — Safety is stated, not implied.** Every completed step names its recoverability in the same breath (`Saved — you can always come back to this point.`), because the founder cannot infer it from a reflog they don't know exists. This is V-5 promoted from failure copy to *all* copy.

---

## 1. The mode toggle (P3-03)

- **Where:** `View → Vibe Mode` plus a two-state segment in the title bar (`Border.SegmentTrack` + `Button.Segment`, the shipped switch vocabulary): `Build` / `Pro`. Mode is view-state, never a data migration; toggling mid-generation interrupts nothing (contract).
- **The collapse:** the dev dock's panels hide; the 2-pane Chat + LivePreview layout renders. The transition is a **Still** swap (M-4's discipline: a mode is state, not spectacle) — the session, agent, and history are identical objects in both modes, and arriving Still *proves* it.
- **Back to Pro:** the full dock restores with the same session intact; the Vibe conversation appears in the Coordinator panel as the same transcript (one history, two renderings — V-6 structurally).

```
┌──────────────────────────────────────────────────────────────────┐
│ MyApp · GitLoom          ⏸ Pause everything    Build|Pro  ─ □ ✕ │
├───────────────────────────┬──────────────────────────────────────┤
│  CHAT                     │  LIVE PREVIEW                        │
│                           │ ┌──────────────────────────────────┐ │
│  ✓ Progress saved         │ │                                  │ │
│    You can always come    │ │      (the founder's running      │ │
│    back to this point.    │ │            app)                  │ │
│                           │ │                                  │ │
│  ◐ Checking your change   │ └──────────────────────────────────┘ │
│    works…                 │  ● Live · updates as it builds       │
│                           │            [Publish to Web]          │
│  › Make the header stay   │                                      │
│    at the top…      [Send]│                                      │
├───────────────────────────┴──────────────────────────────────────┤
│ Working on: sticky header · saved 2 min ago        [details ⌄]  │
└──────────────────────────────────────────────────────────────────┘
```

Layout: chat 38% / preview 62% (the founder's *app* is the subject; the chat is the instrument), 8 px gutter splitter, both panes `SurfacePanel` cards. Spacing steps up one notch across Vibe (panel padding 20, card gaps 15 — the top of the fixed scale, never off it): the same system, breathing more slowly.

### 1.1 The kill switch, translated

The Control Center's `Stop all` renders here as **`⏸ Pause everything`** — same fixed position, same control, same daemon RPC, renamed in the dialect (D-V1: "kill switch" is our word, never theirs). Same interaction spec as ControlCenterDesign §5.4 (quiet at rest, Danger on hover, no confirm, instant). Engaged state banner, in dialect: `Everything is paused. Nothing was lost — your work is saved. Resume whenever you're ready.`

### 1.2 The status line

The footer is the mode's one persistent readout: current task in plain words + the last checkpoint's relative time + `[details ⌄]`. The details expander opens the technical view (terminal tail, event log) *in place* — the founder who grows curious finds the real instrument underneath, which is the product's honesty made spatial (V-6): nothing is hidden, it is *folded*.

---

## 2. The chat — orchestrator events as friendly cards (P3-03)

The founder's messages and the agent's replies render as the Control Center chat does (left-aligned blocks, no bubbles) — but **orchestrator events** (OPS §3.4) render as **status cards**: compact, glyph-anchored, in dialect. The translation table is the design contract:

| OPS event / state | Card glyph (E1 form) | Card copy (the dialect of record) |
|---|---|---|
| checkpoint created (P3-01) | ring + check, `SuccessBrush` | `Progress saved` + `You can always come back to this point.` (D-V2) |
| `queue_state → Verifying` | half disc, `InfoBrush` | `Checking your change works…` (live, ticks Still) |
| verification green | ring + check, `SuccessBrush` | `Checked — everything still works.` |
| verification failed (repair loop running) | triangle, `WarningBrush` | `Something broke — fixing it now.` + `Your last saved point is safe.` |
| `conflict_auto_resolved` | ring + check, `SuccessBrush` | `Sorted out a tangle between two changes.` |
| `conflict_escalated` / breaker trip | triangle, `WarningBrush` | opens the triage screen (§3) — the card is its permanent record: `Needed your help — you chose "Go back to when it worked".` |
| `rate_limited` | ring + bar, `TextMuted` | `Taking a short breather (the AI service asked us to slow down). Back in ~40s.` |
| `budget_exceeded` | ring + bar, `WarningBrush` | `Today's budget is used up. Raise it in Settings, or continue tomorrow.` |
| `egress_denied` | octagon, `TextMuted` | *(not shown as a card by default — a security telemetry line is Pro-mode furniture; it appears under [details] and in the audit trail. Alarming a founder with "blocked pastebin.com" they can't evaluate is noise, not honesty — the event remains fully recorded.)* |
| deploy progress (P3-04) | half disc → check | §4's sequence |

Card mechanics: fixed-slot glyph (E3), Title-weight first line, `TextMuted` second line, radius 8, `SurfaceCard`. Cards arrive with one Settle-140 (they are arrivals, D1 row-11 family); progress cards' text ticks Still. No card ever uses an exclamation mark (V-2 survives translation).

**Anthropomorphism boundary (the V-3 line, drawn exactly):** cards are *chrome* and speak as the instrument — no "I", no "we", no feelings (`Progress saved`, never `I saved your progress!`). The *agent's own chat replies* are model output and may speak naturally in first person — the founder is, after all, talking to something. The card/reply distinction is visible (cards have glyph + card chrome; replies are plain text), so the product never puts feelings in the product's mouth.

**Delight moment — the first checkpoint.** The founder's very first `Progress saved` card appends one extra line, once per project: `That's automatic — it happens after every change.` The moment the safety model clicks is the moment the product is trusted; it costs one sentence and no motion.

---

## 3. The escalation triage (P3-02) — the most important screen

When the circuit breaker trips, the chat pane's composer region yields to the triage card (inline, not a modal — the preview and history stay visible; the founder is never walled off from their own app). Full strength from frame one (D5 — this is the hazard-class surface).

```
┌ Hit a snag ────────────────────────────────────────────┐
│ The last few attempts didn't work — the same error    │
│ kept coming back. Your app is safe: the last saved    │
│ point still works.                                     │
│                                                        │
│ ┌────────────────────────────────────────────────────┐ │
│ │ ↻  Try a different approach                        │ │
│ │    Starts fresh with what we learned from the      │ │
│ │    failed attempts.                                │ │
│ ├────────────────────────────────────────────────────┤ │
│ │ ⟲  Go back to when it worked                       │ │
│ │    Returns to your last saved point that passed    │ │
│ │    its checks — 12 minutes ago.                    │ │
│ ├────────────────────────────────────────────────────┤ │
│ │ ?  Get help                                        │ │
│ │    Packages what happened (with secrets removed)   │ │
│ │    so a person can look at it.                     │ │
│ └────────────────────────────────────────────────────┘ │
│ Show technical details ⌄                               │
└─────────────────────────────────────────────────────────┘
```

- **Exactly three actions**, contract-verbatim labels, rendered as full-width option rows (radius 8, `SurfaceCard`, hover Shift) — **not** three buttons in a row: each option carries a one-line consequence sentence (V-4's shape: what happens, what's kept), and options-with-consequences read as considered choices, not a button bar to guess at. No `Button.Danger` anywhere: none of the three is destructive (even restore is journaled, P3-01) — the calm is structural.
- **Header copy:** names the fact without blame or jargon (`The last few attempts didn't work`) and states safety in the same breath (D-V2). Never a stack trace, never an error class name in the primary layer.
- **Honest disabled state** (contract): with no `VerifiedGreen` checkpoint, option 2 renders disabled at full opacity difference (0.60 stop) with its explanation *in place of* its consequence line: `There's no saved point that passed its checks yet — this appears after the first one.` (TT-2's rule promoted into the visible layer, because hover is not a Vibe-safe channel.)
- **Repeated escalations** (contract): option 1's consequence line changes to steer honestly: `This has happened 3 times — "Get help" may be the faster path.`
- **"Show technical details"** expands inline: breaker state, last error text, checkpoint list — real, unsoftened, mono. The expander label never changes (no "advanced mode" gatekeeping tone).
- **Choosing any option** collapses the card to its permanent chat record (§2 table) — the triage leaves a trace, because a system that hides its stumbles teaches distrust (V-6).

**Five themes:** the card is `SurfaceCard` + `WarningBrush` triangle + text roles — every pairing A1/A4-gated in all five; on Daylight the triangle's retuned `#7E5D07` reads as ink, not alarm, which is exactly the register.

---

## 4. One-click deploy (P3-04) — "Publish to Web"

### 4.1 The flow

`[Publish to Web]` is the preview pane's one `Button.Accent` (the pane's emphasized action; the chat's accent is the composer). First run opens a single sheet:

1. **Where** — provider choice as two plain rows (Vercel / Netlify) with one dialect line each (`Free to start · takes about a minute`), then the provider's own OAuth in the system browser (loopback+PKCE; the sheet states `You'll approve this in your browser — GitLoom never sees your password.`, V-6).
2. **Publishing** — the sheet becomes a four-step checklist, each step a §2-style card line ticking through: `Saving your progress ✓ · Sending your app ◐ · Building… · Going live` — Draw-honest (M-6: real states from `PollStatus`, never a fake bar; unknown duration = the half-disc, not a percentage).
3. **Live** — the URL card (§4.2).

Re-publish skips to step 2 (`same project, new deploy` per contract). Publish is **always explicit** — never automatic on checkpoint (contract invariant, and the copy on the button never implies otherwise).

### 4.2 The live-URL card — the product's emotional peak

```
┌ Your app is live ──────────────────────────────┐
│ https://myapp.vercel.app          [Copy] [Open]│
│ Published just now · updates only when you     │
│ publish again                                  │
└────────────────────────────────────────────────┘
```

One Settle-140 (the earned arrival, D1: this is the founder's *ship moment* — the biggest moment in their product life gets the same 140 ms as everyone else's, because the restraint IS the brand; the URL itself is the fireworks). The URL is `TextBlock.Mono`, selectable; `[Open]` is `Button.Secondary` (the accent was spent getting here). The second line kills the most common founder anxiety (does every change go live?) before it forms (D-V2's spirit).

The card pins to the top of the preview pane thereafter (`● Live` chip + URL), and the chat gets its permanent record: `Published to myapp.vercel.app.`

### 4.3 Failure

A failed build routes into the §3 triage pattern verbatim (contract): header `Publishing didn't finish`, the same three actions (option 1 = `Try publishing again`), provider log tail redacted under the same `Show technical details ⌄`. Never raw provider logs in the primary layer.

---

## 5. The live preview pane (P3-03)

- **Chrome:** none beyond a 1 px hairline card — no fake browser toolbar (the anti-reference: a toy browser inside an app is Electron cosplay). The pane's furniture is one footer line: `● Live · updates as it builds` + the port chip when several dev servers exist (`:5173 ▾`, a `Border.RefChip` picker — contract's port picker).
- **Navigation events:** `[APP_READY_ON_PORT_X]` swaps the pane from its waiting state to the app — Exchange-140 (one truth replacing another: the waiting card out, the app in).
- **Waiting state** (before first ready event): centered, calm: `Your app will appear here — usually under a minute the first time.` on `SurfaceDeep` (the editor surface: this pane is *content*, not chrome).
- **Crash/blank navigation:** the reload affordance renders as a quiet overlay line at the pane's top — `The preview lost its connection. [Reload preview]` (`Button.Secondary`) — the session is unaffected and the copy says nothing scarier than what happened (contract + V-2).
- **Hot reload:** repaints Still. The founder's own app supplies all motion here; the frame never competes.

---

## 6. Conformance record (the Part 2 self-gate)

- **Contract coverage:** P3-03 (toggle = view-state segment, Still swap, same session both directions; chat cards from the event table; preview + port picker + reload; terminal behind [details]); P3-02 (three actions verbatim, consequence-line pattern, honest disabled state with in-place explanation, repeated-escalation steering, no raw traces, expander); P3-04 (provider sheet, OAuth honesty line, checklist with real states, live-URL card, re-publish, triage-patterned failure, explicit-only publish).
- **Voice:** every string passes the five-question gate; the dialect rules D-V1/D-V2 are extensions, not exceptions — V-1/2/3/5/6/7 all hold (the anthropomorphism boundary is drawn at card-vs-reply, §2). Strings feed Microcopy.md's [Horizon] section alongside ControlCenterDesign §9.4.
- **Tokens/classes:** zero new tokens; `Border.SegmentTrack`/`Button.Segment` (toggle), `SurfaceCard`/`SurfacePanel`/`SurfaceDeep` stepping, `Border.RefChip` (port/live chips), `Button.Accent` once per pane (composer / Publish), the §9.3 ring-family glyphs reused on cards. Spacing stays on-scale (15/20 — Vibe breathes at the scale's top, never off it).
- **Motion:** card arrivals Settle-140; triage Still-from-frame-one (D5); checklist Draw-honest (M-6); preview Exchange-140; the ship moment deliberately budget-bound (§4.2). Reduced motion: all collapse to Still, meaning intact (D6 — every card is words + glyph first).
- **Five themes:** all pairings are gated vocabulary; Vibe is *not* a sixth theme — a founder on Daylight Loom and a founder on Midnight get the same warmth, because the warmth is in the words and the space (the D7 argument extended to emotional design).
