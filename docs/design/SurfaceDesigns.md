# Mainguard Surface Designs — Elevating the Five Core Surfaces

**Status: DESIGN SPEC (Lane B Part 1) — no live file is edited by this document.**

This is the surface-design layer above the foundation. It redesigns the five surfaces a daily user
lives in — the review cockpit, the commit graph, the diff viewer, the staging panel, and the
first-run path — for information hierarchy, reduced cognitive load, and premium feel, all on the
[Design System foundation](DesignSystem.md) (Lane A). It is **binding on**: the Part 1 lane palette
and gates **G1–G5**, the Part 2 encoding gates **E1–E4** with the signature triad
(solid / hollow / fractured shield), the severity triad (octagon / triangle / circle), and the
solid-vs-hollow diff bars. Where DesignSystem Parts 3–4 are still stubs, the Master Brief rules are
applied directly: WCAG 2.1 AA, motion 120–150 ms / no bounce / opacity-and-brush only.

Above this document sit [`DESIGN.md`](../../DESIGN.md) and [`PRODUCT.md`](../../PRODUCT.md) (where
they disagree, they win). Strings come from [`docs/creative/Microcopy.md`](../creative/Microcopy.md)
— the final-strings inventory — and are quoted, never re-invented; voice rules are cited from the
[Voice & Delight Bible](../creative/Mainguard_Voice_And_Delight_Bible.md) (`V-#`/`E-#`/`C-#`/`T-#`/
`TT-#`/`ES-#`/`M-#`/`N-#`); motion briefs defer to
[`docs/creative/MotionPlaybook.md`](../creative/MotionPlaybook.md); polish items already specced in
[`docs/creative/PolishSpec.md`](../creative/PolishSpec.md) are ratified by reference, not restated.
First-run sequencing conforms to [`docs/creative/Onboarding.md`](../creative/Onboarding.md).

Non-negotiables inherited by every section:

- **One design system, five switchable themes** — Midnight Watch (default), Day Watch (light),
  Command Deck, Atelier, Aurora. Never assume "dark"; every design below states how it reads in
  all five.
- **No raw colors** — every color is a semantic token bound via `{DynamicResource}`; recurring
  visuals use the `App.axaml` component classes (`Button.*`, `Border.*`) by role.
- **Fixed scales** — radius 6/8/12/999; spacing 4/5/8/10/15/20; the DESIGN.md §3 type ramp
  (10–11 label · 12–13 body · 14 emphasis · 16–18 title · 24 hero).
- **The Quiet Gatehouse north star** — quiet, layered, exactly one signature accent per view.
  Anti-references: never the VS-Code-extension/Electron look, never enterprise-SaaS card-grids or
  hero-metric scaffolding.

**Scope.** Every surface here is the *shipped single-user client*. The agent-era review cockpit
(P2-11: risk-ranked hunks, provenance chips) belongs to the Control Center design (Lane E) and is
deliberately not designed here — but §1 notes where today's cockpit leaves room for it, so the two
designs meet instead of colliding (Design Principle 5).

**How each section is built.** Current state (verified against the real `.axaml`, with line
references) → information architecture → primary flow → mockup → empty/loading/error states → the
one signature accent → the five-theme reading → the exact tokens and classes.

---

## 1 · The Review Cockpit — `RepoDashboardView`

*The daily-driver reason to open Mainguard: one screen where a change is seen, judged, staged, and
committed without leaving.*

### 1.1 Current state, verified

`RepoDashboardView.axaml` is already structurally right — three floating `Border Classes="Card"`
panels on `SurfaceWindow`, separated by transparent 8 px `GridSplitter` gutters (lines 31–57),
with the diff card overriding to `SurfaceDeep` (line 45) exactly as DESIGN.md §5 prescribes. Four
gaps keep it below premium:

1. **The toast is off-system and off-voice.** Lines 11–17 define `Border.toast` /
   `Border.toast.error` filling the pill with `SuccessBrush` or `DangerBrush`. Bible **T-3** is
   explicit: *a failure that needs a decision is a dialog, not a toast* — an error toast that
   auto-dismisses is the exact anti-pattern, and a success toast painted `SuccessBrush` is
   color-as-volume (**V-2**). Microcopy §2 already rehomes every error string into panels.
2. **The loading state is a spinner-with-caption, not a skeleton.** Lines 22–27 center
   `Parsing Repository...` (Title Case mid-sentence + ellipsis-as-suspense, against **V-8**) over an
   indeterminate bar. The product register calls for skeletons that anticipate the layout, not
   center-stage waiting text.
3. **The cockpit has no attention model.** Staging (left), diff (right), and timeline (bottom) each
   own a selection; nothing tells the eye which selection is *driving* the diff right now. The
   selected file row in `StagingPanelView` and the selected commit row in `CommitTimelineView` both
   render selection, so two "current things" can appear equally lit at once.
4. **Fixed 350 px staging column** (line 34) ignores narrow windows; the splitter helps but the
   default proportion is arbitrary rather than reasoned.

### 1.2 Information architecture

The cockpit is one instrument with three gauges, ranked by the question the user asks:

| Rank | Zone | Question it answers | Surface |
|---|---|---|---|
| 1 | **Evidence** — diff card (right, largest) | *what exactly changed?* | `DiffViewerView` on `SurfaceDeep` |
| 2 | **Intent** — staging card (left) | *what will I do about it?* | `StagingPanelView` on `SurfacePanel` |
| 3 | **Record** — timeline card (bottom) | *what has already happened?* | `CommitTimelineView` on `SurfacePanel` |

The elevation already encodes this: the diff sits on the deepest surface (`SurfaceDeep`) because it
*is* the content; the two `SurfacePanel` cards frame it. The redesign keeps the geometry and adds
the missing attention model:

**The focus contract.** At any moment exactly one panel is the *driver* — the panel whose selection
the diff is currently rendering. Selecting a file in staging makes staging the driver (diff shows
the working-tree change); selecting a commit in the timeline makes the timeline the driver (diff
shows that commit's version). The driver's selected row carries the full selection treatment
(`AccentSelection` fill + the 3 px `AccentBrush` rail, DESIGN.md §5); the *non-driver* panel's last
selection drops to `SurfaceHover` fill with **no rail** — still findable, visibly not in command.
One rail on screen, ever. This is a pure style-state (a `driving` pseudo-class on the row style);
no layout changes, and the rail column stays reserved in both panels so the handoff shifts nothing
(**M-2**).

**Proportion.** Default the staging column to `320` (min `280`) and let the diff take the
remainder; the bottom timeline keeps the `7*/3*` split. The 350 px default exists for no stated
reason; 320 fits `filename + path` at Body 12 with the FileRow checkbox and returns 30 px to the
evidence zone.

### 1.3 Primary flow — the daily loop

1. Watcher (`RepositoryWatcher`) or `F5` surfaces changed files in staging — list updates in place,
   selection preserved (**M-2**).
2. `↓`/`↑` through the staging list; the diff follows each file instantly. Staging is the driver;
   its rail is lit.
3. Stage hunks/lines in the diff (§3), or whole files via the row checkboxes.
4. `Ctrl+Enter` — the T-30 scan clears (or gates), the commit lands.
5. The timeline re-paints with the new tip; the toast pill confirms
   `Pushed 3 commits to origin/feature.`-style facts per Microcopy §4. The user never left the
   screen — that is the cockpit's whole argument.

### 1.4 Mockup

```
┌ SurfaceWindow ────────────────────────────────────────────────────────────────┐
│ ┌─ Card: SurfacePanel (staging) ──┐  ┌─ Card: SurfaceDeep (diff) ───────────┐ │
│ │ [Commit|Stash]      ⟳ ↶        │  │ src/Services/GitServices.cs  +42 −7  │ │
│ │ ▾ Changes 4                     │  │ [Unified|Split|Editor]          […]  │ │
│ │ ▐ ☑ GitServices.cs  Services/   │  │ @@ -118,6 +118,9 @@   [Stage][Disc.] │ │
│ │   ☑ IGitService.cs  Services/   │  │  118  context line                   │ │
│ │   ☐ AGENTS.md                   │  │  119 +added line        (DiffAdded)  │ │
│ │ ▸ Untracked 1                   │  │  120 −removed line    (DiffRemoved)  │ │
│ │ ───────────────────────────────  │  │  …                                   │ │
│ │ [Message|Conventional]  □ Amend │  │                                      │ │
│ │ ┌ commit message ┐              │  └──────────────────────────────────────┘ │
│ │ (findings strip — all clear)    │        ↑ 8px transparent gutters ↑        │
│ │ [Commit]  [Commit and push…]    │                                           │
│ └─────────────────────────────────┘                                           │
│ ┌─ Card: SurfacePanel (timeline) ──────────────────────────────────────────┐  │
│ │ branches │  ⌕ filter strip          graph ┊ rows            │ detail rail│  │
│ └──────────────────────────────────────────────────────────────────────────┘  │
│                                            ( Merged feature into main. )  ←toast
└────────────────────────────────────────────────────────────────────────────────┘
▐ = the one lit selection rail (the driver)          ( … ) = radius-999 pill
```

### 1.5 States

- **Empty** (no repo): owned by §5 — the cockpit never renders without a repo.
- **Loading** (repo parse): replace the caption-and-bar with a **skeleton of the cockpit itself** —
  the three `Border.Card` outlines at their real positions, each holding two or three
  `SurfaceCard` rounded-8 placeholder blocks at rest (no shimmer — a sweeping highlight is
  decoration, off the M-3 budget). One `ProgressBar IsIndeterminate` (AccentBrush on `SurfaceCard`,
  height 4) sits at the top edge of the timeline card. The skeleton teaches the layout before the
  data arrives; when it resolves, content cross-fades in once at ~130 ms (**M-3**), no stagger.
  Caption, if any, is `Reading history…` (sentence case, **V-8**) in `TextMuted` Body 12.
- **Error** (invalid repo / `index.lock`): never a toast. The `MainWindow` error overlay carries the
  Microcopy §1.1 `index.lock` string (path in `TextBlock.Mono`, `Button.Secondary` "Retry", no
  severity chip). The cockpit stays rendered behind the scrim — the user's bearings are part of the
  recovery (**V-5**).
- **Toast** (the one legitimate transient): a single neutral pill — radius 999, `SurfacePanel`
  fill, hairline border, `TextPrimary` text, the soft overlay `BoxShadow` — bottom-right, fade in
  ~140 ms, rest, fade out (**T-1**, **M-1**). Delete `Border.toast.error` and the
  Success/Danger-filled variants: outcome is carried by the words (`Merged feature into main.`),
  never by a colored pill shouting it (**V-2**, **T-3**). Undo-bearing toasts (branch delete,
  profile delete) add the one `Button.Secondary` action (**T-2**).

### 1.6 The signature accent

**The driver rail** — the single 3 px rounded `AccentBrush` rail marking the selection that is
currently driving the diff. It is the cockpit's cursor: follow the rail, find the thing the
evidence describes. Everything else on the surface is neutral (`Button.Primary` / `Secondary` /
semantic); the staging panel's `Button.Accent` "Commit" (§4) lives *inside* the staging card and is
that view's own accent — the cockpit chrome itself adds none. One rail, one lit thread, per the
warp-thread logic of DesignSystem G5.

### 1.7 Across the five themes

| Theme | How the cockpit reads | Watch-item |
|---|---|---|
| Midnight Watch | Charcoal cards (`#14171C`) on void (`#0F1115`), violet rail `#8B8BF5` | reference look |
| Day Watch | Paper cards (`#F7F8FB`) on `#EDEFF4`; the surface *step* is subtle in light — the 1 px `BorderHairline` carries the card edges, so hairlines must never be dropped from a card | skeleton blocks must use `SurfaceCard #FFFFFF` + hairline, or they vanish into the panel |
| Command Deck | Near-black tactical (`#0E1114`), ice-teal rail `#2DD4BF` | teal rail vs `SuccessBrush #34D399`: the driver rail and a Success button are close in hue — the rail's *shape* (3 px bar, not a fill) is the separator; never add a Success-filled row anywhere near the rail column |
| Atelier | Warm umber (`#1D1A16`), copper rail `#D8A25A` | copper vs `WarningBrush #D9B04C` are near-twins — a warning banner inside the cockpit must always lead with the `WarningIcon` triangle (E1), never a bare amber strip |
| Aurora | Indigo night (`#161930`), aurora-teal rail `#4FD1C5` | the luminous accent family is bright; keep `AccentSelection`'s low alpha as the fill so selected rows don't glow |

### 1.8 Tokens & classes

`Border.Card` (radius 12, `SurfacePanel`, hairline) · `SurfaceDeep` override on the diff card ·
transparent 8 px `GridSplitter` gutters · selection = `AccentSelection` fill + 3 px `AccentBrush`
rail (driver) vs `SurfaceHover` fill (non-driver) · skeleton blocks `SurfaceCard` radius 8 ·
loading bar `AccentBrush`/`SurfaceCard` · toast pill radius 999 `SurfacePanel` + `BorderHairline` +
`TextPrimary` (+ `Button.Secondary` for Undo) · overlay scrim `#C0000000` (allowed literal) ·
type: Body 12–13, caption `TextMuted`.

---

## 2 · The Commit Graph — `CommitGraphCanvas` + the timeline center pane

*The signature component (DESIGN.md §5): the 60 fps vector weave that is the first-run hook and the
everyday map.*

### 2.1 Current state, verified

The canvas itself is the system's crown: virtualized, vector-drawn, `PenLineCap.Round`, Lane1–Lane5
per DesignSystem Part 1 (gates G1–G5 govern the palette; nothing here re-tunes a hue). The *frame
around it* is where the cognitive load lives — `CommitTimelineView.axaml`:

1. **The toolbar is a filter dump.** Lines 55–107: a 200 px search box, three placeholder
   `ComboBox`es (`Branch`, `User`, `Date`), a `Paths` flyout button, then three icon buttons — eight
   controls of equal visual weight in one strip. Which filters are *active* is invisible once a
   flyout closes: a `ComboBox` with a selection looks nearly identical to one without.
2. **The view-options flyout is a kitchen sink.** Lines 130–191: SHOW / FILTER / COLUMNS /
   **SIGNING** / HIGHLIGHT in one scroll. Commit *signing configuration* (key, format, GPG program —
   repository behavior, T-15) is buried inside a *view* options flyout of the *timeline* — a user
   looking to configure signing will never find it here, and a user opening view options is
   confronted with cryptography. Signing config belongs beside the thing it signs: the commit
   composer's own options (§4), leaving view options to SHOW/COLUMNS/HIGHLIGHT.
3. **A disabled menu item as documentation.** Lines 115–116: the "Git Log Indexing" flyout renders a
   five-line explainer as a permanently disabled `MenuItem` — a control that looks broken and reads
   as UI debt. Explanation belongs in a tooltip on the enable item (**TT-1**).
4. **Row metadata fights the message.** Lines 260–266: Author (`120` px), an absolute
   `dd/MM/yyyy HH:mm` date (`140` px), and the **full SHA truncated by an 80 px column** — a
   truncated full hash is precision theater; `ShortSha` (7-char mono) is the system's stated form
   (**N-6**). Absolute timestamps force mental subtraction; the question a graph answers is *how
   long ago*, so relative time (`3 h ago`, `2 d ago`) with the absolute form in the tooltip serves
   the read better and drops ~60 px per row.
5. **The signature badge block** (lines 248–252) is `13×13` with a collapsing holder — superseded
   verbatim by DesignSystem §2.3/§2.7: the solid/hollow/fractured triad at 12×12 in an
   always-present holder (E2/E3). Ratified here, not restated.

### 2.2 Information architecture

Three zones, left to right, in reading order of the question *"where is the work?"*:

| Zone | Content | Width behavior |
|---|---|---|
| **Refs rail** (left, collapsible) | branch/tag tree + search | 250 default, collapses to nothing — power users navigate refs from the `Ctrl+P` palette and the navbar pill |
| **The weave** (center, dominant) | filter strip → graph + rows | takes all remaining space |
| **Detail rail** (right) | selected commit: message, checks badge, ref chips, meta, files | 300 default |

**The filter strip, rebuilt as chips.** One search field (`TextBox.searchBox`, watermark
`Text or hash`) plus a single `Filter` ghost button. Choosing Branch/Author/Date/Paths from its
flyout emits a **filter chip** into the strip — a radius-999 `AccentSelection` pill,
`AccentBrush` text, with a dismiss `×` (`Border.RefChip` construction, E4: the chip carries its own
label, `Author: daniel`, `Since: 2 weeks`). Active state is now *visible at rest* — the strip reads
like a sentence describing the current view, and clearing it is one click per clause. No chips = no
filters = the full weave; the strip never reflows the graph (chips live in the fixed-height
toolbar row).

**View options, halved.** SHOW / COLUMNS / HIGHLIGHT stay (they are honest view state). FILTER's
"Current Branch Only" moves into the filter flyout where filters live. SIGNING moves out entirely
(§4.2). The indexing button becomes a normal enableable item whose explanation is its tooltip.

**Row anatomy** (24 px min-height, unchanged):

```
[graph 100] [chips] [badge-slot 12] message……………… [rel-time] [author] [sha7]
```

- Chips: `Border.RefChip` (+`.tag`, +`.head`) exactly as shipped — the head chip's polarity
  inversion (solid `AccentBrush` + `OnAccent` text) is already the E1-passing HEAD marker.
- Badge slot: DesignSystem §2.3 triad, 12×12, holder always present (E3).
- Message: Body 13 `TextPrimary`, trimmed; highlighted commits use weight 600, never a color.
- Rel-time: Label 11 `TextMuted`, right-aligned, absolute form in the tooltip (**TT-1**).
- Author: Label 11 `TextMuted`; SHA: `TextBlock.Mono` 12 `TextMuted`, `ShortSha`.

### 2.3 Primary flow

1. Open repo → the graph paints **composed** — full history, no entrance animation, no staggered
   lane draw (**M-2**); motion is scroll.
2. Scan lanes: Lane1 is the trunk (the warp thread, G5 — kin to the theme's accent, so "the main
   line" reads as *this theme's thread* without implying status).
3. Click a row → `AccentSelection` + rail; the detail rail fills; the timeline becomes the cockpit
   driver (§1.2) and the diff shows the commit.
4. Narrow: type in search or add filter chips; the graph re-routes composed, never animated.
5. Act: right-click a row (context menu via `GraphHitTester`), or drag one ref chip onto another —
   the ghost chip (`Border.RefChipGhost`) follows the pointer and the drop flyout offers the two
   named operations, `Rebase feature onto main` / `Merge feature into main` (**C-3**).

### 2.4 Mockup

```
┌ timeline card ────────────────────────────────────────────────────────────────┐
│ refs ▸│ ⌕ Text or hash   (Author: daniel ×) (Since: 2w ×)  [Filter ▾]  ⟳ 👁 │
│───────┼───────────────────────────────────────────────────────────┬───────────│
│ ⌕     │ │╭─╮  (main)(HEAD) 🛡 wire up settings      3 h  daniel a1b2c3d│ detail   │
│ local │ ││ ●                                                      │ rail:     │
│  main │ ││ │╲ (feature)  ⛨ add lfs pointer parse    5 h  sam  9f8e7d6│ message   │
│  feat…│ ││ ● │                                                    │ [✓ 3 passed]│
│ remote│ ││ │ ●  fix blame cache                     1 d  kai  4c5d6e7│ chips     │
│ tags  │ ││ ●╱   merge: lane router                  2 d  daniel …  │ author·date│
│       │ │╰─╯                                                      │ sha7 · files│
└───────┴───────────────────────────────────────────────────────────┴───────────┘
🛡 solid = verified · ⛨ hollow = untrusted (DesignSystem §2.3) · (…) = RefChip
```

### 2.5 States

- **Empty** (fresh repo): ES card per Microcopy §6 — Hero 24/600 `No commits yet`, body
  `Make your first commit from the staging panel to start the history.`, no action button (the
  staging panel is on screen; **ES-1**).
- **Loading** (history walk on a huge repo): rows are virtualized, so the honest state is the
  composed graph of what has loaded plus the toolbar's existing `PathIcon.spinning` refresh glyph.
  No skeleton rows, no shimmer — the graph must never appear to animate itself into being (**M-2**,
  **M-6**).
- **Error** (walk failed): a panel strip above the rows — `WarningIcon` triangle + the Microcopy §2
  generic fallback (`That Git operation didn't complete. Mainguard made no partial change…`) +
  `Button.Secondary` "Retry". Never a toast (**T-3**).
- **Filtered-to-nothing**: Title 16/600 `No commits match these filters` + body naming the chips +
  a plain link `Clear filters` — the state names its own exit (**V-5**).

### 2.6 The signature accent

**The HEAD marker** — the one solid-`AccentBrush` chip (`Border.RefChip.head`, `OnAccent` text) on
the current branch tip, echoed by the navbar branch pill. Everything else in the weave is Lane1–5
(identity, not status — G1–G5) and neutral text. The eye finds *you are here* in one saccade
because it is the only accent-filled object on the surface. Selection's rail is the cockpit-level
accent (§1.6) and appears only on interaction; at rest, HEAD is the single lit point.

### 2.7 Across the five themes

The lane threads are the theme's voice (DesignSystem §1.4 — the exact palettes, ratified):

| Theme | The weave's character | Watch-item |
|---|---|---|
| Midnight Watch | jewel tones: violet warp `#9A9AF4`, rose, pale mint, burnt ember, twilight cobalt | Lane5 `#5066B4` is the darkest thread (3.34:1 vs panel) — never render it thinner than the 2 px pen |
| Day Watch | ink on paper: indigo `#3232E2`, deep magenta, pine, umber, cerulean | `AccentSelection` tint is faint on `#F7F8FB` — the selection rail carries the state (already reserved) |
| Command Deck | tactical traces: ice-teal warp `#7AE7D9`, electric violet, signal orange, sand, deep-sea | the head chip (`#2DD4BF` fill) sits near the Lane1 warp `#7AE7D9` by design (G5 kinship) — its *solid fill + OnAccent text* polarity is what separates marker from thread |
| Atelier | craftsman's bench: cream warp `#E3C8A2`, plum, sage, indigo slate, verdigris | cream lanes on umber are low-chroma — keep the graph column at 100 px so threads never compress below legibility |
| Aurora | luminous night: aurora-teal warp `#79E0D5`, violet, fuchsia, pale gold, cobalt | pale-gold Lane4 `#FDF0D7` is near-white — fine on `#161930`, but any future light-surface chart reuse must re-gate (G3) |

### 2.8 Tokens & classes

`Lane1`–`Lane5` (DesignSystem Part 1 values) · `Border.RefChip`/`.tag`/`.head`/`.dropTarget` +
`Border.RefChipGhost` · signature triad icons `SignatureVerifiedIcon`/`SignatureWarningIcon`/
`SignatureBadIcon` at 12×12 (`SuccessBrush`/`WarningBrush`/`DangerBrush`) · filter chips =
radius-999 `AccentSelection` + `AccentBrush` text · `TextBox.searchBox` · `Button.IconButton`
(ghost on `SurfaceHoverGhost`) · selection `AccentSelection` + 3 px `AccentBrush` rail ·
`TextBlock.Mono` for SHAs · `WarningIcon` on the error strip · type: Body 13 message, Label 11
meta, Mono 12 SHA.

---

## 3 · The Diff Viewer — `DiffViewerView` (unified + side-by-side)

*The surface developers stare at longest; precision here is the product's claim made visible.*

### 3.1 Current state, verified

The bones are strong — hunk-structured unified view with click-and-drag line selection, a
resolver-style side-by-side, intra-line emphasis via `IntraLineDiffTextBlock`, image/LFS/binary
special states. The chrome betrays it:

1. **Off-scale, inline-styled toolbar.** Lines 48–64: the `Ignore Whitespace` / `Syntax` /
   view-mode / `Code Editor` toggles are hand-styled `ToggleButton`s — inline `Background`/
   `Foreground`/`BorderBrush`, **`CornerRadius="4"`** (not on the 6/8/12/999 scale) and
   **`Padding="12,6"`** (12 and 6 off the spacing scale). Four roughly-equal toggles where the
   design system already owns the pattern: `Border.SegmentTrack` + `Button.Segment` (the
   Commit/Stash switch, App.axaml lines 264–282).
2. **No file identity.** The header shows only mode toggles, right-aligned; the file being diffed
   is named nowhere on the surface — the user must glance back at the staging panel to confirm
   *what* they are reading. A diff without its filename is evidence without a caption.
3. **No line numbers.** Neither the unified rows (lines 114–133) nor the side-by-side rows
   (197–219) render old/new line numbers; the hunk header's `@@ -118,6 +118,9 @@` is the only
   coordinate, in raw patch syntax. Line numbers are how a reader cross-references an editor,
   a review comment, or a stack trace.
4. **The conflict bar is alarmist and off-voice.** Lines 258–273: `WarningIcon` painted
   `DangerBrush`, bold Danger `Merge Conflicts Detected:` with a colon, Title Case. Microcopy
   §1.4/§2 own these strings; severity rides the role, not the typography (**V-2**).
5. **The empty state is not the ES pattern.** Lines 70–75: 14 px `Select a file to view changes`.
   Microcopy §6 final: headline `No file selected`, body
   `Choose a changed file in the staging panel to see its diff here.`
6. **The change-margin bars are brush-only** — remedied by DesignSystem §2.5 (added = solid
   `SuccessBrush` bar; modified = hollow 1 px `AccentBrush` outline), ratified here.

### 3.2 Information architecture

```
header   →  file identity (left) · view controls (right)
body     →  hunks: [hunk bar → lines] repeated
footer   →  selection action bar (appears only with a selection)
```

**Header, left side — the caption line:** `PathIcon` (file-type, `TextMuted` per DesignSystem audit
row 6) + the path in `TextBlock.Mono` 12 (`TextMuted` directory + `TextPrimary` filename) + the
change summary `+42 −7` (Label 11 — `SuccessBrush`/`DangerBrush` *text with the +/− glyphs*, so the
numbers survive grayscale, E1).

**Header, right side — controls by frequency:** one `Border.SegmentTrack` with
`Button.Segment` × 3: `Unified | Split | Editor` (the primary mode is a mode, not three unrelated
buttons). The two quality toggles (`Ignore whitespace`, `Syntax`) move into one trailing
`Button.IconButton` overflow (`…` `MenuFlyout`) — they are set-and-forget preferences, not
per-glance controls (the PolishSpec §2 collapse pattern, applied to this strip). `Save file`
appears only in Editor mode as the strip's sole `Button.Primary`.

**The hunk bar** (radius 0 inside the card, `SurfaceCard` fill): coordinates + actions.
Left: `@@ 118–126` — humanized range, mono 12, `AccentBrush` (the raw `@@ -118,6 +118,9 @@` stays
in the tooltip for patch literates, **TT-1**). Right: `Stage` (`Button.Success`) /
`Unstage` (`Button.Primary`) / `Discard` (`Button.Danger`) at `Padding="10,5"` — on-scale, verb
labels (**C-3**); Discard confirms per Microcopy §3.3 with `Stash instead` inside the dialog
(**C-4**).

**Line rows gain a number gutter:** unified gets two right-aligned mono-11 `TextMuted` columns
(old · new; blank on the side that lacks the line); side-by-side gets one column per pane. The
gutter is fixed-width (reserved, E3), `SurfaceDeep` background, hairline right edge — line paint
(`DiffAddedBg`/`DiffRemovedBg`) starts after it, so the numbers stay on a stable ground and the
eye can rule a straight line down the coordinates.

### 3.3 Primary flow

1. A file is selected (staging or timeline drives it, §1.2); the caption line names it instantly.
2. Read hunks top-down; intra-line emphasis (`DiffAddedEmphasis`/`DiffRemovedEmphasis`) points at
   the changed words; trailing whitespace shows via `DiffWhitespaceMarker`.
3. Act at the right grain: whole hunk (hunk-bar buttons) → or click/drag lines and use the
   footer bar (`Stage selected lines` `Button.Success` · `Discard selected lines` `Button.Danger` ·
   `Clear` `Button.Secondary`).
4. Toggle `Split` for a rewrite-heavy file; block actions ride each hunk bar as the icon trio
   (checkmark/undo/dismiss — already glyph-distinct, E1-passing).
5. `Editor` mode for the direct fix; `Save file` stages the save in the conflict flow.

### 3.4 Mockup (unified)

```
┌ Card: SurfaceDeep ─────────────────────────────────────────────────────────┐
│ ▤ src/Services/ GitServices.cs   +42 −7      [Unified|Split|Editor]  […]  │
│─────────────────────────────────────────────────────────────────────────── │
│ @@ 118–126  ······························  [Stage] [Discard]              │
│ 118 118 │  context line                                                    │
│ 119     │− removed line            ← DiffRemovedBg, word emphasis          │
│     119 │+ added line              ← DiffAddedBg                           │
│ 120 120 │  context line                                                    │
│ @@ 240–261 ·······························  [Stage] [Discard]              │
│▐240     │− old call                ← selected: AccentSelection + 4px rail  │
│▐    240 │+ new call                                                        │
│──────────────────────────────────────────────────────────────────────────── │
│                 [Stage selected lines] [Discard selected lines] [Clear]     │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 3.5 States

- **Empty** (no file): ES card — `DocumentIcon` 48 `TextMuted`, Hero headline `No file selected`,
  body `Choose a changed file in the staging panel to see its diff here.` (Microcopy §6), no
  button (**ES-1**).
- **Loading** (diff compute / whitespace re-diff): keep the previous diff visible, dimmed to
  0.6 opacity, with the header's spinner glyph; swap composed when ready — never a blank flash
  between files (**M-2**; the cancellation pattern `DiffViewerViewModel` already uses).
- **Error / notice band** (partial-staging staleness, lines 82–89 — kept): `SurfaceCard` strip,
  `WarningIcon` triangle in `WarningBrush` + `TextMuted` text. It already passes E1; ratified.
- **Conflict state** (Editor mode): the bar becomes calm — `SeverityBlockerIcon` (octagon,
  `DangerBrush`, 14) + `TextPrimary` text per Microcopy §2:
  `The merge stopped at 3 conflicted files. Nothing is committed yet…` + `Button.Accent`
  "Open resolver" (the view's one accent belongs to the way forward here) + `Button.Secondary`
  "Abort merge". No bold red headline, no colon (**V-2**, **E-3**).
- **Binary / LFS / image**: the shipped centered summaries are the right shape; align their text to
  the ES ramp (headline Title 16, body Label 12 `TextMuted`) and keep `Stored with Git LFS` as the
  caption.

### 3.6 The signature accent

**The selection paint** — `AccentSelection` line fill + the 4 px rounded `AccentBrush` rail on
selected diff lines, culminating in the footer action bar. Partial staging at line grain is
Mainguard's precision claim; the accent marks exactly the lines the user has claimed and nothing
else. Hunk-header coordinates drop from `AccentBrush` to `TextMuted` mono (they are wayfinding,
not action) so the accent stays reserved for selection — one accent, one meaning. (In the conflict
state, where selection is absent, the accent hands off to "Open resolver".)

### 3.7 Across the five themes

| Theme | Diff ground | Watch-item |
|---|---|---|
| Midnight Watch | `SurfaceDeep #0B0D10`, adds `#11271B` / removes `#33191E` | reference; `TextPrimary` on both tints ≥ 4.5:1 holds |
| Day Watch | white editor `#FFFFFF`, adds `#DDF3E4` / removes `#FBE2E2` | the pastel tints are close in lightness — the `+`/`−` glyph column and the §2.5 margin bars are the non-color channel; verify `TextPrimary #1A1E27`-family on both tints ≥ 4.5:1 (it clears; keep body text `TextPrimary`, never `TextMuted`, on tinted lines) |
| Command Deck | `#07090B` deep, teal accent | `Stage` (`Button.Success #34D399`) beside teal-accent selection rail: hue-close — rail = bar, button = filled label, shapes disambiguate (E1) |
| Atelier | `#121009` deep warm | `WarningBrush #D9B04C` vs accent copper `#D8A25A` are near-identical — the staleness band *must* keep its triangle icon; never encode "warning" by amber text alone here |
| Aurora | `#0C0E1A` deep indigo, removes `#351A26` plum-tinted | plum remove-tint is subtle — the hollow/solid margin bars (§2.5) carry change-kind if tint reads ambiguous |

### 3.8 Tokens & classes

`SurfaceDeep` card ground · `Border.SegmentTrack` + `Button.Segment` for `Unified|Split|Editor` ·
`Button.IconButton` overflow · `Button.Success`/`Primary`/`Danger` hunk actions at `Padding 10,5` ·
`Button.Secondary` "Clear"/"Abort merge" · one `Button.Accent` only in the conflict state ("Open
resolver") · `DiffAddedBg`/`DiffRemovedBg` line tints · `DiffAddedEmphasis`/`DiffRemovedEmphasis` ·
`DiffWhitespaceMarker` · margin bars per DesignSystem §2.5 (solid `SuccessBrush` / hollow
`AccentBrush`) · selection `AccentSelection` + `AccentBrush` rail · `WarningIcon` /
`SeverityBlockerIcon` · number gutter `TextMuted` mono 11 on `SurfaceDeep` + `BorderHairline` ·
`FontMono` 13 line text, `LineHeight 20`.

---

## 4 · The Staging Panel — `StagingPanelView`

*Intent central: what goes into history, and the gate that keeps secrets out of it.*

### 4.1 Current state, verified

1. **Untracked files scream danger.** Lines 149–151: the untracked list paints both the file-type
   glyph *and the filename* in `DangerBrush`. An untracked file is not a hazard — this is
   DesignSystem audit row 5's confirmed semantic-brush misuse, and the loudest V-2 violation in the
   app: a brand-new user's first `.gitignore`-less repo renders as a wall of red. Remedy (ratified
   from DesignSystem §2.7): glyph → `TextMuted`, filename → `TextPrimary`, in both lists.
2. **Vocabulary drift.** The header says `Unversioned Files` (line 125) — Git says *untracked*, and
   the toolbar segment says `Shelf` while every Microcopy string says *stash*
   (`Stash instead`, `Stashed 4 files. Restore them from the Stash list.`). **N-6** (one term per
   concept) rules: `Changes` / `Untracked` for the lists, `Commit | Stash` for the segment,
   `Stash message…` / `Stash changes` in the stash tab.
3. **The rebase state is a red alarm.** Lines 207–224: centered bold `DangerBrush`
   "Merge Conflicts Detected", an instruction paragraph, then three stacked buttons — `Resolve
   Conflicts` as `Button.Accent`, `Continue Rebase` as `Button.Primary`, and an unclassed button
   with `DangerBrush` foreground for Abort. Microcopy §1.4 owns the copy; the button roles need the
   same discipline.
4. **Off-scale paddings**: `Padding="15,6"` on the commit buttons and `Padding="2,2"` on category
   headers (lines 194–201, 77) — 6 and 2 are off the 4/5/8/10/15/20 scale → `15,5` and `4,4`.
5. **Composer label drift**: the commit watermark `Commit Message (Hit Enter for Description)` is
   Title Case with a parenthetical aside (**V-8**) → `Commit message — first line is the subject`.

### 4.2 Information architecture

Top-to-bottom, the panel is a funnel narrowing toward one button:

```
1  toolbar     [Commit | Stash]                    ⟳  ↶
2  the ledger  ▾ Changes 4        (tri-state select-all)
               ▸ Untracked 1
3  the gate    findings strip (T-30) — octagon/triangle/circle triad
4  the intent  [Message | Conventional]  □ Amend last commit
               message box / structured composer
5  the act     [Commit]  [Commit and push…]
```

- **The ledger** keeps the shipped checkbox model (checked = will be committed) — it is the
  panel's working vocabulary and the tri-state headers already summarize it. Rows: `CheckBox.FileRow`
  · file-type glyph (`TextMuted`, 18-wide reserved) · filename `TextPrimary` 12 · directory
  `TextMuted` 11. Identical anatomy in both lists — state is carried by *which list*, per the
  section headers (E1 passes via structure, not tint).
- **The gate** — `PreCommitFindingsView` stays embedded directly above the commit row, so a
  blocker physically interrupts the path to the button. Severity rows use the DesignSystem §2.4
  triad (octagon blocker / triangle warning / circle info, 10×10) — ratified.
- **Signing lives here now** (from §2.2): a small `Signing…` item inside the composer's options
  (reachable from the `Conventional`/`Message` row's trailing `Button.IconButton` overflow),
  carrying the T-15 key/format/program fields. Configuration sits beside the act it modifies; the
  timeline keeps only the *display* toggle (`Show signature status`).
- **The stash tab** keeps its card list; `Pop`/`Apply` stay `Button.Primary`, `Drop` stays
  `Button.Secondary` with the tolerated `DangerBrush` foreground (the documented exception), and
  Drop's confirm follows pattern C.

### 4.3 Primary flow

1. Edits appear in `Changes` via the watcher — in place, no reflow of selection (**M-2**).
2. Review each file (§1.3 loop); check what belongs in the commit; hunk/line-stage the rest away
   in the diff (§3).
3. Write the message — plain box or the Conventional composer (live preview, char counter amber
   past 72 via `WarningBrush`).
4. The scan runs on commit: all-clear shows the quiet strip; a blocker pauses with
   `Blocker: an AWS secret key was found in src/config.ts:42. Committing would write it into
   history.` (**V-2**) and the explicit `Commit anyway` / `Cancel` pair.
5. `Ctrl+Enter` lands it. The tree drains toward the clean state; the affirmation appears
   (§4.5) — the loop closes.

### 4.4 Mockup

```
┌ Card: SurfacePanel ────────────────────────────┐
│ [Commit ▎Stash]                        ⟳  ↶   │
│ ▾ ☑ Changes 4                                  │
│ ▐ ☑ ▤ GitServices.cs      Services/            │  ← selected (driver rail §1.6)
│   ☑ ▤ IGitService.cs      Services/            │
│   ☐ ▤ AGENTS.md                                │
│ ▸ ☐ Untracked 1                                │  ← TextPrimary, not Danger
│ ───────────────────────────────────────────────│
│ [Message ▎Conventional]   □ Amend last commit ⋯│  ← ⋯ = composer options (signing)
│ ┌─────────────────────────────────────────────┐│
│ │ wire up settings                            ││
│ └─────────────────────────────────────────────┘│
│ ⬢ Blocker: an AWS secret key was found in      │  ← octagon (DesignSystem §2.4)
│    src/config.ts:42 …    [Commit anyway][Cancel]│
│ [Commit]  [Commit and push…]                   │  ← the one Accent · Primary
└────────────────────────────────────────────────┘
```

### 4.5 States

- **All-clear** (clean tree): the earned quiet affirmation (**ES-4**) — Hero `Working tree clean`,
  body `Every change is committed.` (Microcopy §6), a 48 px `CheckmarkIcon` in `TextMuted` (not
  Success — quiet, not celebratory), fade-in only (**M-1**). No action button: done is done.
- **Loading** (refresh): the toolbar's `PathIcon.spinning` refresh glyph; the lists never blank.
- **Error** (identity missing, commit failure): inline panel strip above the commit row with the
  Microcopy §2 string (e.g. `Every commit is stamped with a name and email, and this repository
  has none set. Add one in Git Profiles…`) + the routing link. Never a toast (**T-3**).
- **Rebase/merge conflict state** (replaces the red block): a `SurfaceCard` radius-8 banner —
  `SeverityBlockerIcon` octagon 14 `DangerBrush` + Microcopy §1.4:
  `This rebase step conflicts in 2 files. Resolve them in the conflict resolver — saving stages
  each file — then Continue rebase. Abort rebase returns the branch to its pre-rebase tip; nothing
  is lost by stopping.` Buttons: `Button.Accent` **Open resolver** → becomes **Continue rebase**
  once the tree is clean (the accent tracks the way forward), `Button.Secondary` **Abort rebase**
  (a documented-safe exit is a cancel, not a destructive act — the copy states the guarantee,
  **C-2**/**C-5**).
- **Discard / clean confirmations**: pattern C verbatim from Microcopy §3.3/§3.4, with
  `Stash instead` inside the discard dialog (**C-4**).

### 4.6 The signature accent

**The Commit button** — the panel's single `Button.Accent`, the funnel's mouth. Everything above
it is neutral or semantic; the one violet fill is the reason the panel exists. In the conflict
state the accent transfers to `Open resolver`/`Continue rebase` (Commit is hidden there), so the
invariant holds: one accent, always on the panel's way forward.

### 4.7 Across the five themes

| Theme | The panel reads as | Watch-item |
|---|---|---|
| Midnight Watch | charcoal ledger, violet Commit | reference |
| Day Watch | paper ledger | the tri-state checkbox glyphs and `BorderHairline` on `#F7F8FB` are the thin things — sweep per PolishSpec §7; `TextMuted #5C6470` on panel ≥ 4.5:1 holds for the directory captions |
| Command Deck | near-black, teal Commit `#2DD4BF` | Commit (Accent) beside a Success-styled action would blur (`#34D399`) — the panel has no Success button at rest, keep it that way; the blocker octagon's `DangerBrush #FB7185` reads pink-warm, silhouette carries (E1) |
| Atelier | warm bench, copper Commit `#D8A25A` | the over-72 counter uses `WarningBrush #D9B04C` ≈ accent copper — pair the counter color change with the count text itself (`74/72`), never color alone |
| Aurora | indigo ledger, aurora Commit | `AccentSelection` over `#161930` is faint — row hover (`SurfaceHover #232849`-family) must stay distinct from selection; the rail disambiguates |

### 4.8 Tokens & classes

`Border.SegmentTrack` + `Button.Segment` (`Commit|Stash`, `Message|Conventional`) ·
`Button.IconButton` toolbar/overflow · `CheckBox` + `CheckBox.FileRow` · file glyph + directory
`TextMuted`, filename `TextPrimary` · findings triad `SeverityBlockerIcon`/`WarningIcon`/
`SeverityInfoIcon` with `DangerBrush`/`WarningBrush`/`InfoBrush` at 10×10 · `Button.Accent`
Commit (or Open resolver/Continue rebase) · `Button.Primary` Commit-and-push, Pop/Apply ·
`Button.Secondary` Cancel/Abort/Drop (Drop keeps the tolerated `DangerBrush` foreground) ·
`Button.Danger` in discard/clean confirms · message box `SurfaceCard` radius 8, focus
`AccentBrush` border (global style) · paddings snapped to `15,5` / `4,4` · stash cards
`SurfaceCard` radius 8 + hairline.

---

## 5 · OOBE / Onboarding — the "aha in 60 seconds" path

*`MainWindow` first run → `CloneDashboardView` → the workspace. The storyboard is
[Onboarding.md](../creative/Onboarding.md); this section designs the surfaces it walks through.*

### 5.1 Current state, verified

1. **The first screen is an account wall.** `CloneDashboardView.axaml` lines 14–22: the
   unauthenticated state leads with a 64 px `GitHubIcon`, `Connect to GitHub`, and an Accent
   `Login with GitHub` — sign-in as the gate to the product. Onboarding §1 step 1 and §4.1 are
   unambiguous: the first surface is the `No repository open` empty state with **Open repository**
   (local folder — no network, no account) as the accent and **Clone from a remote** as the quiet
   alternative. A public HTTPS clone needs no auth either; GitHub sign-in is progressive
   disclosure, not a wall.
2. **Clone is browse-first, not URL-first.** The authenticated state is a `WrapPanel` of 150 px
   repo cards (lines 54–106) — an identical-card grid (the named anti-pattern) whose real job is
   repo *search*, plus there is no visible paste-a-URL path at all. The engineer arriving from a
   README's clone box has a URL in the clipboard; the primary clone affordance must be a URL
   field.
3. **Off-voice confirmations.** Lines 130–141: `Reclone Repository?` /
   `Are you sure you want to clone it again…` / `Clone Anyway` as `Button.Accent` — "Are you sure"
   violates **C-1**, and the confirm carries an Accent where the pattern demands the verb button
   carry the action's own role.
4. **Clone progress feel** — the bar snaps per reported percent (the `TODO(T-21)` at lines
   117–119). PolishSpec §5's brief (130 ms `DoubleTransition` on `Value`, linear/ease-out, then
   the `Cloned react into ~/code/react.` pill) is ratified verbatim.
5. **Login card copy** (`Sync your cloud repositories and clone them with one click.`) reads
   marketing-register in-app (**V-3**/**V-7**).

### 5.2 Information architecture — three screens, one thread

```
Screen A  MainWindow first run          Screen B  CloneDashboardView            Screen C  the workspace
┌────────────────────────┐             ┌───────────────────────────┐          (RepoDashboardView §1)
│      ◇ (Mainguard mark 64)  │             │ Clone a repository        │
│  No repository open    │   Clone…    │ ┌ Remote URL ───────────┐ │  clone   the graph paints
│  Open a folder that's  │  ────────►  │ └───────────────────────┘ │ ───────► composed at 60fps —
│  a Git repo, or clone  │             │ ┌ Destination folder ───┐ │          the hook (M-2), then
│  one from a remote.    │             │ └───────────────────────┘ │          the first commit —
│  [Open repository]     │             │ [Clone]                   │          the aha (§5.4)
│   Clone from a remote  │             │ ── or browse your host ── │
└────────────────────────┘             │  Connect GitHub  (quiet)  │
                                       └───────────────────────────┘
```

**Screen A** — the ES card, verbatim from Microcopy §6: Hero 24/600 `No repository open`, body
`Open a folder that's a Git repo, or clone one from a remote.`, `Button.Accent`
**Open repository**, `Button.Secondary` **Clone from a remote**. One 64 px empty-state glyph in
`TextMuted`. Window content fades in once at ~130 ms; nothing else moves (**M-3**). No tour, no
checklist, no preselection (Onboarding §4).

**Screen B** — clone, URL-first. The form card (`SurfacePanel`, radius 12, hairline, centered,
max-width ~480): `Remote URL` field (radius 8 `SurfaceCard`, focus → `AccentBrush` border — the
global style) with paste-detection filling a suggested destination; `Destination folder` picker;
`Button.Accent` **Clone**, disabled until valid, tooltip
`Enter a remote URL and choose an empty folder to clone into` (**TT-2**). Below a hairline: the
host-browse section — one quiet line `Or browse your GitHub repositories` +
`Button.Secondary` **Connect GitHub**. After sign-in the section becomes a *list* (rows: lock
glyph when private · `owner/name` mono-adjacent emphasis 13 · description `TextMuted` 12 · trailing
`Button.Primary` Clone; already-cloned rows swap the button for a `Button.Secondary` **Open**) —
rows, not a card grid: this is a picker, and rows scan faster than tiles. A private-HTTPS URL with
no token routes to the host sign-in instead of failing blind (**V-5**).

**Progress overlay** — the shipped radius-12 `SurfacePanel` card over the `#C0000000` scrim is
right; add the PolishSpec §5 easing and phase-named status (`Receiving objects — 12,480 of 18,006`
→ `Checking out files`, **M-6** honest-or-absent), `Button.Secondary` **Cancel clone** (canceling
a not-yet-created thing is a cancel, not a destructive act — the current `Button.Danger` overstates
it). On error, the Microcopy §2 string (`The folder mainguard/ already has files in it, so Mainguard
won't clone over them. Pick an empty folder or a new name.`) appears inline under the bar,
`DangerBrush` text on the message only, no icon theatrics.

**Reclone confirm, repaired** (pattern C): Title `Clone react again?` · body
`This repository is already cloned at ~/code/react. Cloning again creates a second, separate copy
— the existing one isn't touched.` · `Button.Primary` **Clone a copy** · `Button.Secondary`
Cancel. No Accent, no "Anyway", no "Are you sure" (**C-1**; not destructive, so no Danger either).

### 5.3 Primary flow — the 60-second script (Onboarding §3, made physical)

| t | Beat | Surface |
|---|---|---|
| 0–5 s | Screen A paints; one fork, nothing preselected | `MainWindow` ES card |
| 5–20 s | Paste URL → Clone; the bar fills monotonically, phases named | Screen B + overlay |
| 20–35 s | **The hook**: the workspace paints composed — the full weave at 60 fps, no spinner-to-content jank | `CommitGraphCanvas` (**M-2**) |
| 35–55 s | Edit a file in any editor → the watcher surfaces it → stage a hunk → `Ctrl+Enter` → scan clears | §3, §4 |
| 55–60 s | **The aha**: the new tip appears, the HEAD chip already on it — no `index.lock`, no terminal, no ceremony | §2 — the absence of the footgun *is* the proof |

The only celebrations on this path: the `Cloned react into ~/code/react.` pill (~140 ms fade,
**M-1**) and the `Working tree clean` affirmation (**ES-4**). Reduced-motion collapses both to
instant state changes (**M-7**).

### 5.4 States

- **Empty**: Screen A *is* the empty state (ES-1/ES-2) — designed above.
- **Loading**: the clone overlay (honest monotonic bar); host-list loading = three skeleton rows
  (`SurfaceCard` blocks) in the browse section, never a full-screen spinner.
- **Error**: inline under the form (invalid URL, non-empty folder, auth-required routing to
  Accounts per Microcopy §2); clone-failure keeps the overlay open with the error and
  `Button.Secondary` Retry / Cancel — the user's typed URL is never discarded.
- **Not-connected** (browse section, no token): quiet section copy
  `Or browse your GitHub repositories` + **Connect GitHub** — an empty state, not an error
  (**ES-3**); no `DangerBrush` anywhere on first run (Onboarding §2's governing rule).

### 5.5 The signature accent

**One accent per beat, always on the single way forward:** `Open repository` (Screen A) → `Clone`
(Screen B) → the `AccentBrush` progress fill (overlay) → the HEAD chip in the freshly painted
weave (Screen C). The violet thread literally pulls the user from empty to instrument — the same
token, handed forward, never two accents on one screen (**ES-2**, One-Accent Rule).

### 5.6 Across the five themes

First run defaults to Midnight Watch; the theme menu (`File → Theme`) is Onboarding step 11's
"glimpse of craft". The path must nonetheless survive any theme, because a returning user's
persisted theme re-renders Screen A:

| Theme | First-run read | Watch-item |
|---|---|---|
| Midnight Watch | the reference welcome — charcoal + one violet action | — |
| Day Watch | paper-light welcome | the ES glyph at `TextMuted #5C6470` and the hairline card edge carry the composition — verify at PolishSpec §7 sweep |
| Command Deck | tactical, ice-teal `Clone` | the progress fill `#2DD4BF` on `SurfaceCard #13171B` clears 3:1 comfortably |
| Atelier | warm workshop, copper `Open repository` | copper Accent + `OnAccent #121009`-family text — AA large-text bound holds; keep button text 13/600 |
| Aurora | luminous night | scrim + `SurfacePanel #161930` overlay card: the hairline `#2A2F55`-family must stay visible over the dimmed backdrop — it does at 1 px, don't thin it |

### 5.7 Tokens & classes

ES card: Hero 24/600 `TextPrimary` + body 12 `TextMuted` + 64 px glyph `TextMuted` ·
`Button.Accent` (Open repository / Clone) · `Button.Secondary` (Clone from a remote / Connect
GitHub / Cancel clone / Cancel) · `Button.Primary` (row Clone / Clone a copy / Open) · form fields
`SurfaceCard` radius 8, focus `AccentBrush` · overlay: scrim `#C0000000` (allowed literal),
radius-12 `SurfacePanel` card, hairline, soft `BoxShadow`, bar `AccentBrush`/`SurfaceCard` with
130 ms `DoubleTransition` · toast pill per §1.5 · browse rows on `SurfaceHoverGhost` rest →
`SurfaceHover` hover · `LockIcon` 12 `TextMuted`.

---

## Appendix A — Cross-surface delta list (for implementers)

The concrete changes this spec asks for, deduplicated. PolishSpec and DesignSystem items already
in flight are marked *(ratified)* and not re-specified.

| # | Surface / file | Change |
|---|---|---|
| 1 | `RepoDashboardView.axaml` 11–17, 61–66 | Delete `Border.toast`/`.error`; one neutral pill (radius 999, `SurfacePanel`, hairline, `TextPrimary`); errors route to panels per Microcopy §2 (**T-3**) |
| 2 | `RepoDashboardView.axaml` 22–27 | Skeleton cockpit loader (three card outlines + `SurfaceCard` blocks + one indeterminate bar); caption `Reading history…` |
| 3 | `RepoDashboardView` + both list styles | The focus contract: rail only on the driver panel's selection; non-driver selection drops to `SurfaceHover`, no rail |
| 4 | `CommitTimelineView.axaml` 55–107 | Filter strip → search + `Filter` flyout emitting dismissible `AccentSelection` chips; active filters visible at rest (E4) |
| 5 | `CommitTimelineView.axaml` 130–191 | View-options flyout keeps SHOW/COLUMNS/HIGHLIGHT (+ `Show signature status`); FILTER merges into the filter flyout; SIGNING config moves to the composer options (§4.2) |
| 6 | `CommitTimelineView.axaml` 115–116 | Kill the disabled-menu-item documentation; explanation becomes the enable item's tooltip (**TT-1**) |
| 7 | `CommitTimelineView.axaml` 260–266 | Row meta: relative time (absolute in tooltip), author Label 11, `ShortSha` mono 12 (**N-6**) |
| 8 | `CommitTimelineView.axaml` 248–252 | *(ratified)* DesignSystem §2.3/§2.7 badge triad, 12×12, reserved holder |
| 9 | `DiffViewerView.axaml` 46–67 | Toolbar → caption line (glyph `TextMuted` + mono path + `+n −n`) left; `SegmentTrack` `Unified|Split|Editor` + `…` overflow (whitespace/syntax) right; kill `CornerRadius="4"` and `Padding="12,6"` |
| 10 | `DiffViewerView.axaml` 97–111, 174–194 | Hunk bar: humanized `@@ 118–126` in `TextMuted` mono (raw form in tooltip); actions `Padding 10,5` |
| 11 | `DiffViewerView.axaml` line templates | Reserved line-number gutters (old/new, mono 11 `TextMuted`, hairline edge) in unified and split |
| 12 | `DiffViewerView.axaml` 70–75 | ES-pattern empty state: `No file selected` + Microcopy body |
| 13 | `DiffViewerView.axaml` 258–273 | Conflict bar → octagon + Microcopy §2 merge-stop string; `Button.Accent` "Open resolver" + `Button.Secondary` "Abort merge" |
| 14 | `DiffViewerView.axaml.cs` margin | *(ratified)* DesignSystem §2.5 solid/hollow bars |
| 15 | `StagingPanelView.axaml` 108, 149–151 | *(ratified + extended)* file glyphs `TextMuted`, untracked filenames `TextPrimary` — no Danger on untracked |
| 16 | `StagingPanelView.axaml` 40–43, 125, 232–233 | Vocabulary: `Commit|Stash` segment, `Untracked` header, `Stash message…`/`Stash changes` (**N-6**) |
| 17 | `StagingPanelView.axaml` 207–224 | Rebase block → `SurfaceCard` banner, octagon 14, Microcopy §1.4 copy; `Button.Accent` Open resolver→Continue rebase, `Button.Secondary` Abort rebase |
| 18 | `StagingPanelView.axaml` 183, 194–201, 77 | Watermark `Commit message — first line is the subject`; paddings `15,5` / `4,4` |
| 19 | `StagingPanelView` composer options | New `⋯` overflow hosting the relocated T-15 signing fields |
| 20 | `CloneDashboardView.axaml` 14–22 | Remove the account wall; Screen B is URL-first with `Connect GitHub` as quiet progressive disclosure |
| 21 | `CloneDashboardView.axaml` 54–106 | Repo card grid → scannable rows (glyph · `owner/name` · description · trailing action) |
| 22 | `CloneDashboardView.axaml` 130–141 | Reclone confirm → pattern C (`Clone react again?`, `Button.Primary` Clone a copy); no Accent, no "Are you sure" |
| 23 | `CloneDashboardView.axaml` 111–128 | *(ratified)* PolishSpec §5 easing + completion pill; Cancel clone → `Button.Secondary` |
| 24 | `MainWindow` first-run | ES card verbatim (Microcopy §6 row 1): `No repository open` + Accent/Secondary pair |

## Appendix B — Self-gate

| Gate | Result |
|---|---|
| Conforms to DesignSystem Part 1 (G1–G5) | Pass — no lane hue invented or retuned; §2.7 quotes the ratified palettes; G5 warp-thread kinship applied to the HEAD-marker rationale |
| Conforms to DesignSystem Part 2 (E1–E4) | Pass — badge triad, severity triad, and diff bars adopted by reference (§2.2 item 5, §3.5, §4.2); new encodings (filter chips, `+n −n`, driver rail) each carry a non-color channel; reserved slots throughout (E3); every status chip carries text (E4) |
| Strings from Microcopy.md, none invented in parallel | Pass — every quoted string is a Microcopy final (§§1.1, 1.4, 2, 3.3, 3.4, 4, 6); the only new strings are structural labels (`Untracked`, `Clone a copy`, `Reading history…`, `No commits match these filters`), each run through the Appendix-A five-question gate |
| One signature accent per surface, named | Pass — driver rail (§1.6), HEAD marker (§2.6), selection paint (§3.6), Commit button (§4.6), the handed-forward accent (§5.5) |
| Empty/loading/error per surface | Pass — §§1.5, 2.5, 3.5, 4.5, 5.4 |
| Five-theme reading per surface | Pass — §§1.7, 2.7, 3.7, 4.7, 5.6, with real token values and the two accent/semantic proximity risks (Command Deck teal≈Success, Atelier copper≈Warning) called out and mitigated by shape/label channels |
| Scales respected; off-scale values only ever *removed* | Pass — every named radius ∈ {6,8,12,999}, spacing ∈ {4,5,8,10,15,20}; the spec deletes `4` radius, `12,6`/`15,6`/`2,2` paddings |
| Motion 120–150 ms, no bounce; WCAG AA applied where Parts 3–4 are stubs | Pass — all motion is the 130–140 ms fade/brush family (M-1/M-3); no skeleton shimmer; contrast bounds checked at the named risk points (§3.7 Daylight tints, §5.6 Atelier OnAccent) with Part 3 as the final authority when authored |
| Anti-references held | Pass — the one card grid found (CloneDashboard) is removed, not restyled; no hero metrics, no eyebrows, no web-view chrome introduced |
| No [Horizon] UI designed | Pass — §1 scope note defers the P2-11 agent cockpit to Lane E |
