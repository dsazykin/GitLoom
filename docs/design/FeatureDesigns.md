# GitLoom Feature Designs — The Client-Parity Features (C1–C5) as Experiences

**Status: DESIGN SPEC (Lane B Part 2) — no live file is edited by this document.**

This is the sibling of [`SurfaceDesigns.md`](SurfaceDesigns.md) (Lane B Part 1, which elevates the
five shipped surfaces — referenced here, never restated). It designs the five *planned* features of
the client-parity track — **P2-C1 interactive bisect**, **P2-C2 global fuzzy search**, **P2-C3
multi-repo dashboard + cross-repo attention lane**, **P2-C4 split-into-branches wizard**, **P2-C5
client polish pack** — end-to-end as experiences: the flow, the surface, the keyboard-first
interactions, the states, and the one delight moment each has earned. Functional contracts come
from [`docs/phase-2/GitLoom_Master_Implementation_Document_v2.md`](../phase-2/GitLoom_Master_Implementation_Document_v2.md)
(P2-C1…C5) and [`docs/planning/GitLoom_Backlog.md`](../planning/GitLoom_Backlog.md) §A; where a
contract and this design disagree on function, the Master Doc wins.

It is **binding on the same foundation as Part 1**: the
[Design System](DesignSystem.md) lane palette and gates **G1–G5**, the encoding gates **E1–E4**
(signature triad, severity triad, solid-vs-hollow bars), and Part 1's own vocabulary — the *driver
rail* and focus contract (SurfaceDesigns §1.2), the *filter chips* (§2.2), the *caption line*
(§3.2), the *funnel* (§4.2), and the *handed-forward accent* (§5.5). Strings come from
[`docs/creative/Microcopy.md`](../creative/Microcopy.md) wherever one exists; every new string is
inventoried in Appendix A and has passed the Voice Bible's five-question gate. Motion defers to
[`docs/creative/MotionPlaybook.md`](../creative/MotionPlaybook.md) (curves G-1…G-10); where
DesignSystem Parts 3–4 are still stubs, the Master Brief rules apply directly — WCAG 2.1 AA,
motion 120–150 ms / no bounce / opacity-and-brush only.

Non-negotiables inherited by every section:

- **One design system, five switchable themes** — Midnight Loom (default), Daylight Loom (light),
  Command Deck, Atelier, Loom Aurora. Never assume "dark"; every feature states its five-theme
  reading.
- **No raw colors** — every color is a semantic token bound via `{DynamicResource}`; recurring
  visuals use the `App.axaml` component classes (`Button.*`, `Border.*`) by role.
- **Fixed scales** — radius 6/8/12/999; spacing 4/5/8/10/15/20; the DESIGN.md §3 type ramp.
- **The Precision Loom north star** — quiet, layered, exactly one signature accent per view.
  Anti-references: never the VS-Code-extension/Electron look, never enterprise-SaaS card-grids or
  hero-metric scaffolding.

**Naming stance (Bible N-2).** All five features take the plain engineering noun: *Bisect*,
*Search*, *Repositories*, *Split into branches*, and the polish items by their Git names. No
strained loom metaphors — the weave appears in how the surfaces *render*, not in what they're
called.

**Scope.** These are single-user client features. P2-C3's future life as the swarm control surface
(P2-13) is Lane E's to design; §3 notes the seams it leaves open and designs nothing speculative
(Design Principle 5).

**How each section is built.** Contract (what the Core layer provides, verified against the
P2-C spec) → information architecture → primary flow → keyboard-first interactions → mockup →
empty/loading/error states → the delight moment → the one signature accent → the five-theme
reading → the exact tokens and classes.

---

## 1 · Bisect — the interactive bisect assistant (P2-C1)

*`git bisect` is the fastest debugging tool most people abandon mid-run. GitLoom turns it into an
instrument reading: the commit graph itself becomes the search space, visibly narrowing until one
commit is left.*

### 1.1 Contract, verified

P2-C1 (Master Doc §P2-C1): `StartBisect(repoPath, badSha, goodSha)` /
`MarkGood`/`MarkBad`/`MarkSkip` (each returns the next candidate + a `BisectState` — remaining
commits, steps left, current SHA, done + culprit) / `ResetBisect` (abort → restore HEAD). CLI via
`RunGitChecked`, pure `BISECT_LOG` parser, **journaled HEAD moves (T-19), dirty-tree refusal**,
culprit context via T-32 (`ICommitContextService`). Every design decision below renders exactly
that state machine — nothing here asks Core for more.

### 1.2 Information architecture — the graph is the instrument

Bisect does not get its own window. A binary search over commits *is a view of the commit graph*,
so the whole session lives in the timeline card (`CommitTimelineView`, SurfaceDesigns §2), which
gains a **session strip** and a **verdict overlay on the weave**:

| Zone | Content | Behavior |
|---|---|---|
| **Session strip** (fixed-height row between the toolbar and the rows) | step counter · the candidate under test · Good/Bad/Skip · Stop bisect | present only while a bisect runs; fixed height, so entering/leaving bisect never reflows the graph (E3 discipline) |
| **The weave** (existing graph + rows) | eliminated commits recede; the candidate carries the selection treatment; boundary verdicts get marker glyphs | pure style-states on existing rows — no new row anatomy |
| **Detail rail** (existing right rail) | setup form → per-step commit detail → the culprit card | the rail's normal job (show the selected commit), specialized per phase |

**The verdict overlay, encoded per E1.** Three redundant channels, none color-only:

- *Eliminated* rows (proven good, or outside the remaining range): row text drops to `TextMuted`
  and the graph threads render at 0.45 opacity — still legible topology (the dimmed thread is
  wayfinding, not data), and the elimination is also carried by the shrinking
  `N commits remain` count in the strip, so opacity is never the sole channel.
- *Marked-good boundary*: a 12×12 `CheckmarkIcon` in `SuccessBrush` in the row's reserved badge
  slot (the DesignSystem §2.3 holder — bisect borrows the slot only while a session runs; signing
  badges are suppressed for the session, their tooltip channel unaffected elsewhere).
- *Marked-bad boundary*: a 12×12 `DismissIcon` in `DangerBrush` in the same slot. Check-vs-cross
  are the app's established verdict glyphs (DesignSystem audit row 7) — distinct silhouettes, E1.
- *Skipped*: the row keeps full opacity but its badge slot carries `PendingIcon` (the DesignSystem
  audit-row-7 ring) in `TextMuted` — "still unknown," which is the truth (N-3).

The navbar branch pill shows `Detached at a1b2c3d` exactly per Microcopy §1.2 — bisect *is* a
detached HEAD, and one term per concept (N-6) forbids a parallel "bisecting" pill. The session
strip is the visible carrier of the bisect state (TT-4).

### 1.3 Primary flow

1. **Entry.** Three doors, one form: the commit context menu (`Bisect from here…` on the known-bad
   commit), the repo actions menu (`Bisect…`), and the palette (`ActionIds.StartBisect`,
   "Start bisect").
2. **Setup** (detail rail, radius-8 `SurfaceCard` form): `Bad commit` (prefilled — HEAD, or the
   right-clicked commit) and `Good commit`, both plain text fields accepting a ref or SHA — and
   while either field has focus, **clicking any row in the graph fills it** (the instrument fills
   its own form). Below the fields, the honest estimate:
   `96 commits between good and bad — about 7 steps.` `Button.Accent` **Start bisect**, disabled
   until both refs resolve (TT-2 tooltip: `Enter a good and a bad commit — a tag or an older
   commit works for good`).
3. **Dirty-tree refusal** (before anything moves — the undo-style guard):
   `Bisect checks out a different commit at each step, and 3 files have uncommitted changes.
   Commit or stash them first.` — inline panel text under the form, `TextMuted`, no alarm (a
   refusal that changed nothing is not an error, V-2).
4. **The loop.** GitLoom checks out the midpoint (journaled). The strip reads
   `Step 3 of ~7 · 12 commits remain · testing a1b2c3d — "wire up settings"`. The user builds/
   tests outside GitLoom, then verdicts: **Good** / **Bad** / **Skip**. The next checkout happens
   immediately; the weave dims the eliminated half in one 130 ms opacity fade (G-2 family); the
   strip updates in place (M-2). During the checkout the three verdict buttons disable and the
   strip shows the `RefreshIcon` spinning glyph — honest in-flight, never a fake bar (M-6).
5. **The culprit.** `BisectState.Done` flips: the strip collapses to `Found the first bad commit.`
   and the detail rail becomes the **culprit card** (§1.6). `End bisect` restores the original
   HEAD (journaled) and clears the overlay.
6. **Any time:** `Stop bisect` (`Button.Secondary`, right end of the strip) aborts —
   `ResetBisect`, HEAD restored, no confirmation dialog: a documented-safe exit is a cancel, not a
   destructive act (the SurfaceDesigns §4.5 "Abort rebase" precedent). Its tooltip states the
   guarantee: `Returns HEAD to main and forgets the session — nothing else changes`.

**Verdict button roles.** Good = `Button.Success`, Bad = `Button.Danger`, Skip =
`Button.Primary`. This is the semantic vocabulary used honestly — the buttons *are* verdicts
(pass/fail), the same meaning the check glyphs carry (DesignSystem audit row 7) — and none of the
three is destructive: a mis-mark's recovery is `Stop bisect` and a restart, which the Skip tooltip
family makes discoverable. The labels are the words, never bare colors (E1/E4): a colorblind user
reads **Good / Bad / Skip**.

### 1.4 Keyboard-first

| Key | Where | Action |
|---|---|---|
| `G` / `B` / `S` | while the session strip is active and focus is in the timeline card | Good / Bad / Skip — single-key verdicts, rendered as gesture chips on the buttons (the `CommandPaletteView` gesture-chip pattern) |
| `Enter` | setup form | Start bisect (when valid) |
| `↑`/`↓` | rows | browse commits freely mid-session — browsing never changes the checkout; only verdict keys act |
| `Ctrl+P` → "Stop bisect" | anywhere | the palette carries the exit too (`ActionIds.StopBisect`) |

Single letters are scoped to the timeline card's focus, never global (a `G` in the commit-message
box must type a G). All three verdicts remain clickable buttons — keyboard-first, not
keyboard-only.

### 1.5 Mockup

```
┌ timeline card ────────────────────────────────────────────────────────────────┐
│ refs ▸│ ⌕ Text or hash                    [Filter ▾]                  ⟳  👁  │
│ Bisect · step 3 of ~7 · 12 commits remain · testing a1b2c3d "wire up…"       │
│        [Good  G] [Bad  B] [Skip  S]                            Stop bisect   │
│───────┼──────────────────────────────────────────────────────────┬───────────│
│       │ │╭─╮ ✕ (main) commit under suspicion      2 h  daniel …  │ Testing   │
│       │ ││ ●   candidate range (full paint)                      │ a1b2c3d   │
│       │▐│ ●   testing a1b2c3d  ← the candidate spotlight         │ message,  │
│       │ ││ ●   candidate range                                   │ author,   │
│       │ ││ ○ ✓ marked good — below here, dimmed to 0.45          │ diffstat  │
│       │ │╰─╯   eliminated rows in TextMuted                      │           │
└───────┴──────────────────────────────────────────────────────────┴───────────┘
✓ = CheckmarkIcon Success · ✕ = DismissIcon Danger · ▐ = the one selection rail
```

### 1.6 The culprit card

The payoff surface, in the detail rail — a radius-8 `SurfaceCard` with a hairline border:

```
┌ SurfaceCard ────────────────────────────┐
│ First bad commit                        │  Title 16/600 TextPrimary
│ wire up settings cache                  │  Body 13 TextPrimary (full message expands)
│ daniel · 3 days ago · a1b2c3d           │  Label 11 TextMuted · SHA mono
│ +42 −7 across 3 files                   │  Label 11 (+/− glyphs carry kind, E1)
│ ⑂ Merged through PR #128 — "Add …"      │  T-32 context row (PullRequestIcon), when resolvable
│                                         │
│ [View diff]  [Create fix branch]  ⧉     │  Accent · Primary · IconButton (copy SHA)
│  End bisect — return to main            │  Button.Secondary
└─────────────────────────────────────────┘
```

The card fades in once at ~140 ms (G-3) — that is the whole celebration (M-1). No toast on top of
it: one moment, one meaning. `View diff` selects the commit and drives the diff card
(SurfaceDesigns' focus contract — the timeline becomes the driver); `Create fix branch` opens the
create-branch dialog pinned to the culprit's *parent* (the last good state — the fix starts from
before the bug, and the dialog's prefill note says so). The T-32 row appears only when the host
resolves it; absent host, the row simply isn't there — never a "connect to see more" upsell inside
a result card (V-3).

### 1.7 States

- **Empty** (no session): nothing — bisect adds zero chrome at rest. The feature's resting state
  is its absence.
- **Setup-invalid**: Start disabled + the TT-2 tooltip (§1.3.2); an unresolvable ref shows inline
  under its field: `No commit found for "v1.o" — check the tag or paste a SHA.` (`DangerBrush`
  text on the field message only, the `DuplicateProfileNameException` inline pattern).
- **Loading** (checkout in flight): verdict buttons disabled + spinning `RefreshIcon` in the strip
  (M-6); the graph never blanks.
- **Error** (checkout fails mid-session — e.g. an untracked file would be overwritten): a panel
  strip replaces the verdict row — `WarningIcon` triangle + `This step couldn't check out
  a1b2c3d — {plain one-line reason}. Fix it and retry, or stop the bisect; stopping returns HEAD
  to main.` + `Button.Primary` "Retry" · `Button.Secondary` "Stop bisect". Never a toast (T-3).
- **Skip-exhausted** (only skipped candidates remain): the strip states the truth —
  `Bisect can't narrow further — the 3 remaining candidates were all skipped. The first bad
  commit is one of them.` The three rows stay fully lit with their `PendingIcon` marks; the detail
  rail lists them as candidate rows. Honest about the machine (V-6): no fake single culprit.

### 1.8 The delight moment

**The narrowing weave.** Each verdict visibly halves the lit region of the graph — eliminated
threads recede to 0.45 opacity in one quiet 130 ms fade while the strip's count drops
(`24 remain` → `12 remain` → `6`…). The user *watches the binary search happen* on the instrument
they already trust. No confetti at the end; the culprit card's single fade is the landing. This is
delight as comprehension — the CLI's invisible state machine, made visible (Design Principle 3).

### 1.9 The signature accent

**The candidate spotlight.** The one commit under test carries the full selection treatment —
`AccentSelection` fill + the 3 px `AccentBrush` rail — and it is the only accent-lit thing in the
card during a session (the HEAD chip rides the candidate anyway, since bisect checks it out). When
the culprit lands, the accent hands forward to the culprit card's **View diff** — the accent
tracks the way forward, the SurfaceDesigns §4.6 invariant.

### 1.10 Across the five themes

| Theme | How the session reads | Watch-item |
|---|---|---|
| Midnight Loom | jewel threads dim to charcoal ghosts; violet spotlight `#8B8BF5` | reference |
| Daylight Loom | ink threads fade toward paper; dimmed 0.45 lanes on `#F7F8FB` sit near the hairline — acceptable because dimmed rows are *eliminated* (wayfinding, not data); the strip's counts carry the state (E1) | verify dimmed `TextMuted` rows still ≥ 3:1 for large glyphs at the PolishSpec §7 sweep |
| Command Deck | Good button `#34D399` beside the teal accent spotlight `#2DD4BF` — the known kinship | the Good *button* is a filled label, the spotlight a fill+rail on a row; shape separates them (the SurfaceDesigns §1.7 mitigation, restated because both are now on one card) |
| Atelier | cream/plum threads recede on umber; the Good/Bad fills use the theme's own `SuccessBrush`/`DangerBrush` (sage/oxblood) | the `WarningIcon` error strip vs copper accent: always icon-led (E1), never amber text alone |
| Loom Aurora | luminous threads dim against `#161930` | Lane4 `#FDF0D7` is near-white at full paint — at 0.45 opacity it remains the brightest dimmed thread; acceptable (eliminated ≠ invisible), no retune |

### 1.11 Tokens & classes

Session strip on `SurfacePanel` (inside the card, radius 0, hairline bottom) · `Button.Success`
(Good) / `Button.Danger` (Bad) / `Button.Primary` (Skip/Retry) / `Button.Secondary` (Stop bisect)
/ `Button.Accent` (Start bisect → View diff, one per phase) · verdict glyphs `CheckmarkIcon`
(`SuccessBrush`) / `DismissIcon` (`DangerBrush`) / `PendingIcon` (`TextMuted`) at 12×12 in the
reserved badge slot (E3) · candidate = `AccentSelection` + 3 px `AccentBrush` rail · eliminated =
`TextMuted` text + 0.45-opacity threads · strip text Body 12, counts invariant-formatted, SHAs
`TextBlock.Mono` · culprit card `SurfaceCard` radius 8 + hairline · `RefreshIcon` spinning glyph ·
`WarningIcon` on the error strip · gesture chips per the palette pattern.

---

## 2 · Search — the global fuzzy search overlay (P2-C2)

*One keystroke, one box, everything: commits, branches, tags, files, PRs, issues — ranked by the
pinned `FuzzyMatcher`, jumped to with Enter.*

### 2.1 Contract, verified

P2-C2: an `ISearchAggregator` fanning a query to local sources (commits via the graph walk,
branches/tags, working-tree files) and host sources when a token exists (PRs via T-23, issues via
T-24); each source returns ranked `SearchHit`s (kind, title, subtitle, jump target) scored by the
T-18 `FuzzyMatcher` (which already returns matched-char positions for highlighting); merged
re-rank, cap, debounce; `Ctrl+Shift+F`; Enter jumps. The overlay below renders exactly that.

### 2.2 Information architecture — the palette's sibling, not its twin

The surface extends the shipped `CommandPaletteView` chrome — the same radius-12 `SurfacePanel`
card over the `#C0000000` scrim with the one soft `BoxShadow`, the same 140 ms entrance (G-4) —
because two overlays with different physics would be two apps. What differs is the content model:

```
┌ query box ────────────────────────────┐   TextBox.searchBox, watermark names the objects
│ (All) (Commits) (Branches) (Tags) (Files) (PRs) (Issues)    ← scope chips, Tab cycles
│ Commits · 12                          │   group header, Label 11 TextMuted
│   ● fix blame cache                   │   rows: icon · title-with-lit-spans · subtitle · dest
│ Branches · 2                          │
│   …                                   │
│ Pull requests ⟳                       │   host groups always LAST — async fill never reflows
└───────────────────────────────────────┘
```

- **Scope chips** (radius-999): `All · Commits · Branches · Tags · Files · PRs · Issues`. The
  active chip fills `AccentSelection` with `AccentBrush` text and carries its own label (E4);
  inactive chips are ghost (`SurfaceHoverGhost` rest). `Tab` / `Shift+Tab` cycles scope;
  the pointer path is a click. In `All`, each group shows its top 5 hits; the group header's
  right side shows the true count (`Commits · 12`) — scoping to the chip shows them all.
- **Result row anatomy** (28 px, virtualized):
  `[kind icon 14, TextMuted] title Body 13 · subtitle Label 11 TextMuted · destination Label 11 TextMuted right-aligned`.
  Kind icons are the shipped set — `CommitIcon`, `TagIcon`, `DocumentIcon`, `PullRequestIcon`,
  `IssueIcon`; branches use the `Border.RefChip` glyph treatment. Subtitles per kind:
  commit `a1b2c3d · daniel · 3 h ago` (mono SHA), file its directory, PR/issue
  `#128 · open`. The destination hint names where Enter lands: `Timeline`, `Diff`, `Blame`,
  `Pull requests`, `Issues` — the jump is never a surprise (V-1).
- **Match highlighting**: the `FuzzyMatcher.Match` positions render in `AccentBrush` **and weight
  600** — the weight is the non-color channel (E1), so the match pattern survives grayscale.
- **Ordering contract**: local groups (Commits/Branches/Tags/Files) render first and appear
  within one debounce tick; host groups (PRs/Issues) are pinned *last*, so their async arrival
  appends below and never shifts a selection the user is already steering (M-2's no-reflow ethos
  applied to data arrival).

### 2.3 Primary flow

1. `Ctrl+Shift+F` anywhere (also the palette row "Search everything" — the two overlays
   cross-reference, never nest). The overlay fades in at 140 ms; the query box has focus.
2. Type. Local results compose per keystroke (debounced); the lit spans show *why* each hit
   matched. Host groups fill in below when their responses land.
3. `↓`/`↑` moves through rows, skipping group headers (the shipped palette's header-skipping
   selection); `Tab` narrows to a scope when one group is the target.
4. `Enter` jumps: a commit selects the row in the timeline (which becomes the cockpit driver,
   SurfaceDesigns §1.2) — a file opens in the diff (or blame via the row's context) — a branch
   checks *nothing* out, it selects the ref in the timeline (navigation never mutates; checkout
   stays an explicit act) — a PR/issue opens its panel to the item. The overlay closes with the
   jump.
5. `Esc` closes; nothing changed, nothing to confirm.

### 2.4 Keyboard-first

| Key | Action |
|---|---|
| `Ctrl+Shift+F` | open (global, in the default `ShortcutMap` — rebindable via T-18 like every gesture) |
| `↓` `↑` | move selection, skipping headers |
| `Tab` / `Shift+Tab` | cycle scope chips (All → Commits → … → All) |
| `Enter` | jump to the selected hit (or the top hit when nothing is selected) |
| `Esc` | close, no side effects |

No modifier-chord grammar (`#` prefixes, `@author` operators) in v1 — the scope chips *are* the
grammar, visible instead of memorized. Power grammar can layer later without moving anything.

### 2.5 States

- **Empty query**: the scope chips + one `TextMuted` Label line under the box:
  `Type to search — Enter jumps to the top result.` No "recent items" feed — an overlay that
  opens full of content the user didn't ask for is noise (V-7).
- **Loading**: local groups appear as they resolve (sub-keystroke); a host group in flight shows
  the `RefreshIcon` spinning glyph beside its header (M-6 honest-or-absent). No skeleton rows in
  an overlay — results simply compose.
- **No matches**: the palette's exact ES pair, shared verbatim (N-6 — one term per concept across
  the two overlays): Title 16 `No matches for "blamecache"` · body
  `Try a shorter query, or press Esc to close.` Query stays focused.
- **Host not connected**: the PR/issue groups render one quiet `TextMuted` line —
  `Sign in to GitHub to include PRs and issues.` — an empty state, not an error (ES-3): no
  icon, no `DangerBrush`, no button inside the overlay (Accounts is one palette action away, and
  an overlay must never spawn a second overlay).
- **Host error** (transient API failure): the same quiet line form —
  `PRs and issues couldn't load — local results are complete.` Honest about what *is* complete
  (V-6); local hits are unaffected.

### 2.6 The delight moment

**The lit thread.** The user's query renders as a literal accent-colored thread woven through
every result — the same five characters lighting up inside a commit message, a branch name, a file
path, and a PR title, in ranked order, faster than the keystroke repeat rate. The loom metaphor
grounded in function (N-2): one thread, pulled through the whole repository. No animation is added
to achieve it — the delight is the ranking quality (the pinned `FuzzyMatcher`) plus native speed.

### 2.7 The signature accent

**The match spans.** `AccentBrush` + 600 weight on matched characters is the view's single accent
use; it marks exactly *why you are seeing this row* and nothing else. Selection reuses the
standard rail treatment (shared with the palette); the scope chips' active fill is
`AccentSelection` tint — the low-alpha companion, not a second accent. One accent, one meaning:
"this is your thread."

### 2.8 Across the five themes

| Theme | The overlay reads as | Watch-item |
|---|---|---|
| Midnight Loom | charcoal card, violet-lit spans `#8B8BF5` | reference |
| Daylight Loom | paper card on the dimmed app; spans `#6467E8` on `#F7F8FB` | `AccentSelection` scope-chip tint is faint on paper — the chip's `AccentBrush` label text (E4) carries the active state; verify chip text ≥ 4.5:1 at the PolishSpec §7 sweep |
| Command Deck | tactical card, ice-teal spans `#2DD4BF` | teal spans inside a Success-word context (e.g. a commit message containing "passed") could read as status — they can't be confused *structurally*: spans are mid-word fragments with 600 weight, never a filled chip |
| Atelier | warm bench card, copper spans `#D8A25A` | copper spans vs `WarningBrush #D9B04C`: no warning UI exists inside the overlay by design (errors render as quiet `TextMuted` lines, §2.5) — the collision is avoided by exclusion |
| Loom Aurora | indigo card, aurora spans `#4FD1C5` | the luminous accent at 600 weight is bright on `#161930` — correct: the spans are the one thing that should glow |

### 2.9 Tokens & classes

Overlay: scrim `#C0000000` (allowed literal) + radius-12 `SurfacePanel` card + `BorderHairline` +
soft `BoxShadow`, 140 ms `DoubleTransition` entrance (G-4) · `TextBox.searchBox` · scope chips
radius-999 — active `AccentSelection` fill + `AccentBrush` text, inactive ghost on
`SurfaceHoverGhost` · rows: kind icons 14 `TextMuted` (`CommitIcon`/`TagIcon`/`DocumentIcon`/
`PullRequestIcon`/`IssueIcon`), title Body 13 `TextPrimary` with `AccentBrush`+600 match spans,
subtitle/destination Label 11 `TextMuted`, SHAs `TextBlock.Mono` · selection `AccentSelection` +
3 px `AccentBrush` rail (reserved column) · group headers Label 11 `TextMuted` · `RefreshIcon`
spinning for in-flight host groups · ES text Title 16 + Body 12 `TextMuted`.

---

## 3 · Repositories — the multi-repo home + the attention lane (P2-C3)

*Polyrepo work means juggling; the Repositories home is one calm ledger of every registered repo —
and one lane of everything that needs you, across all of them.*

### 3.1 Contract, verified

P2-C3: `WorkspaceOverviewService` reports per registered repo — current branch, ahead/behind vs
upstream, dirty/clean, stash count, last-fetched — via `ExecuteWithRepo`, cached, refreshed on
`RepositoryChanged`/auto-fetch (the T-10 cadence with the existing per-repo overlap guard);
persisted repo set (repo bookmarks + `WorkspaceCategory` already in `AppDbContext`); quick
Fetch/Pull/Open; **plus the needs-attention lane** aggregating host items across repos —
review-requested PRs, assigned issues, failing checks — from the shipped T-23…T-27 services. The
Master Doc names this the Copilot-"My Work"/GitKraken-Launchpad parity view; this design keeps the
job and rejects both looks.

**The anti-reference, addressed head-on.** The backlog sketch says "dashboard grid of repo
cards" — an identical-card grid is the named banned pattern (PRODUCT.md, and the one instance
found in the app was removed by SurfaceDesigns §5.2). This surface is therefore a **ledger of
rows**, the staging panel's proven vocabulary at repo scale: rows scan vertically, align their
columns, and never truncate a repo name to fit a tile.

### 3.2 Information architecture

Suggested pair: `WorkspaceOverviewView` / `WorkspaceOverviewViewModel` (paired via `ViewLocator`),
hosted where `CloneDashboardView` lives today. Routing: with zero registered repos, first run
keeps Onboarding's Screen A verbatim (`No repository open` — nothing here touches the 60-second
path); with one or more, the Repositories home is the launch surface, and the workspace's nav
offers one quiet way back (a `Repositories` crumb at the navbar's left — plain noun, N-2).

Two zones on `SurfaceWindow`, split by the standard 8 px transparent gutter:

| Zone | Question | Surface |
|---|---|---|
| **The ledger** (left, dominant) | *what state is my fleet in?* | `Border.Card` on `SurfacePanel` — category-grouped repo rows |
| **Needs attention** (right rail, 300 default) | *what needs me, anywhere?* | `Border.Card` on `SurfacePanel` — host items grouped by kind |

**Ledger row anatomy** (32 px, virtualized, columns aligned across rows):

```
▐ react        (main)   ↑2 ↓1   3 changed · 2 stashed   fetched 5 min ago   ⟳ ⤓ Open
```

- Repo name: Body 13 `TextPrimary`, weight 600 — the row's anchor.
- Branch: the shipped `Border.RefChip` (a detached repo shows `Detached at a1b2c3d`, Microcopy
  §1.2 — the pill vocabulary is global).
- Ahead/behind: `ArrowUpIcon`/`ArrowDownIcon` 10×10 + count, **both in `TextMuted`** — a
  deliberate refusal of the GitKraken-style green/red counts: ahead and behind are *facts about
  synchronization*, not verdicts, and painting them as status is exactly the Semantic-Not-Literal
  violation Part 1 of the DesignSystem exists to prevent. In sync shows nothing (quiet is the
  healthy reading). Glyph + number is the E1 channel.
- Working state: `3 changed · 2 stashed` — Label 11 `TextMuted`, text-carried (E4), zero when
  clean (absence reads as clean; the row needs no "✓ clean" chatter, V-7).
- Last-fetched: Label 11 `TextMuted`, dimming past 15 min with the Microcopy §5 stale-fetch
  tooltip verbatim (`Last fetched 22 min ago — ahead/behind counts may be out of date`).
- Quick actions: three ghost `Button.IconButton`s (`RefreshIcon` fetch · `ArrowDownIcon` pull ·
  `Open` as a small `Button.Primary`) — **always visible**, never hover-revealed: hover-only
  affordances are invisible to keyboard flow (the TT-4 principle applied to controls).

Category headers (`WorkspaceCategory`): the staging panel's collapsible `▾ Work 6` header pattern,
`4,4` padding. Uncategorized repos sit in a trailing group with no invented label.

**The attention rail**: three kind groups, in severity order — **Failing checks**
(`DismissIcon` in `DangerBrush`), **Review requested** (`PullRequestIcon` in `TextMuted`),
**Assigned issues** (`IssueIcon` in `TextMuted`). Row anatomy:
`[kind icon 14] react — #128 Add personal intake form · 2 h ago` — repo-qualified title Body 12,
rel-time Label 11 `TextMuted`. Enter/click opens that repo *and* routes to the item's panel (PR
review section, Issues panel, Checks window) — one gesture from "something needs me" to standing
in front of it. Only failing checks carry a semantic brush; a review request is work, not a
warning (V-2). The lane is named **Needs attention** — it names what is true (N-3); "My Work" is
the parity term, not the label.

### 3.3 Primary flow

1. Launch → the home paints composed: every registered repo's cached status instantly, refresh
   sweeping in place as `WorkspaceOverviewService` re-reads (no row ever blanks, M-2).
2. Scan the ledger top-down: dirty rows and behind-counts pop against the quiet clean majority
   *by having content at all* — the empty-cell design makes anomaly the only ink.
3. Scan (or arrive from) **Needs attention**; Enter on an item lands inside the right repo, on
   the right panel.
4. `Enter`/Open on a repo row → the full workspace (`RepoDashboardView`). The Repositories crumb
   returns; state is preserved (the home is cheap reads, never a reload ceremony).
5. Add: `Button.Accent` **Add repository** (toolbar, the view's one accent button) → open a local
   folder or route to the clone form (SurfaceDesigns §5.2's Screen B). Remove: row context menu →
   `Remove from this list?` confirm — body states the guarantee:
   `This only removes the bookmark — the repository on disk isn't touched.` `Button.Primary`
   "Remove" (nothing destructive happens; C-3's verb rule still applies).

### 3.4 Keyboard-first

| Key | Action |
|---|---|
| `↑`/`↓` | move through repo rows (and across group headers, skipping them) |
| `Enter` | open the focused repo |
| `F` / `P` | fetch / pull the focused repo (row-scoped; disabled states get TT-2 tooltips, e.g. `Pull needs an upstream — this branch tracks no remote`) |
| `Ctrl+Shift+F` | global search (§2) — scoped to the open repo today; the home wires it when the aggregator grows a multi-repo source (noted seam, not designed) |
| `Tab` | move between the ledger and the attention rail |
| `Ctrl+P` | the palette carries `Add repository`, `Fetch all`, and every repo by name (`Open react`) |

`Fetch all` exists only as a palette/toolbar action, not an anxious auto-behavior — auto-fetch
already runs on the T-10 cadence; the button is for the "I just got on the train Wi-Fi" moment.

### 3.5 Mockup

```
┌ SurfaceWindow ─────────────────────────────────────────────────────────────────┐
│ Repositories                                     [Fetch all]  [Add repository] │
│ ┌ Card: ledger ────────────────────────────────────┐ ┌ Card: Needs attention ─┐│
│ │ ▾ Work 3                                         │ │ Failing checks · 1     ││
│ │ ▐ react      (main)  ↑2 ↓1  3 changed  5 min ago │ │ ✕ react — CI / build   ││
│ │   gitloom    (phase2)       2 stashed  2 min ago │ │    failed · 20 min ago ││
│ │   api-core   (Detached at a1b2c3d)     22 min ago│ │ Review requested · 2   ││
│ │ ▾ Personal 1                                     │ │ ⑂ api-core — #52 Add … ││
│ │   dotfiles   (main)             clean  1 h ago   │ │ ⑂ react — #128 Person… ││
│ │                                                  │ │ Assigned issues · 1    ││
│ │                                                  │ │ ◔ gitloom — #77 Blame… ││
│ └──────────────────────────────────────────────────┘ └────────────────────────┘│
└─────────────────────────────────────────────────────────────────────────────────┘
▐ = the focused row's rail · row actions (⟳ ⤓ Open) omitted for width, always rendered
```

### 3.6 States

- **Empty** (no repos — only reachable by removing the last one, since first run routes to
  Screen A): ES card — Hero 24/600 `No repositories yet`, body
  `Add a local repository or clone one to see its status here.`, `Button.Accent`
  "Add repository" (ES-1/ES-2).
- **Loading** (first read of a newly added repo): the row renders its name immediately with three
  `SurfaceCard` radius-8 placeholder blocks where the columns will be — the skeleton *is* the row
  (G-5 family), one cross-fade when the status lands. Never a whole-surface spinner.
- **Row error — folder missing** (moved/deleted path): the row keeps its name; the meta line
  reads `Folder not found at ~/code/react` (`TextMuted`, path mono) with `Button.Secondary`
  "Locate…" and the remove path in the context menu. Not `DangerBrush` — a moved folder is a
  chore, not a hazard (V-2).
- **Row error — fetch failed**: `WarningIcon` 12 (`WarningBrush`) leading a Label
  `Fetch failed — check Remotes` in the fetched-slot (icon + text, E1), with the full pattern-E
  string in the tooltip. Other rows are untouched; a per-repo failure never becomes a global
  banner.
- **Attention rail, not connected**: quiet ES (ES-3) — Title 16 `Nothing to gather yet`, body
  `Connect GitHub to see review requests, assigned issues, and failing checks across your
  repositories.`, `Button.Secondary` "Open Accounts" (the Accent is spoken for by
  Add repository — One-Accent Rule).
- **Attention rail, all clear** (the earned one): Title 16 `Nothing needs attention`, body
  `No review requests, assigned issues, or failing checks right now.` — fade-in only (ES-4).

### 3.7 The delight moment

**The loom at rest.** Every repo clean, nothing behind, the attention rail reporting
`Nothing needs attention` — the ledger goes almost entirely quiet because the design spends ink
only on anomalies. The reward for being caught up is a nearly empty instrument, arrived at with a
single ~140 ms fade of the all-clear state (M-1's ES-4 license). The inverse delight is the
morning scan: one glance ranks the day — a failing check above two review requests above an
assigned issue — with zero navigation.

### 3.8 The signature accent

**The focused thread.** One repo row carries the `AccentSelection` + 3 px rail at a time — the
repo you are about to enter — and opening it hands the same accent forward into the workspace
(the navbar branch pill, the HEAD chip), continuing SurfaceDesigns §5.5's handed-forward token.
The `Add repository` button is the view's one `Button.Accent`; the rail is selection state, not a
second accent (the same coexistence as the staging panel's Commit + selected row).

### 3.9 Across the five themes

| Theme | The home reads as | Watch-item |
|---|---|---|
| Midnight Loom | charcoal ledger, violet focus rail | reference |
| Daylight Loom | paper ledger on `#EDEFF4` | column alignment is carried by hairlines and whitespace — the group headers' `BorderHairline` under-rule must not be dropped (SurfaceDesigns §1.7's Daylight rule) |
| Command Deck | near-black fleet board — the closest this app comes to "mission control," carried entirely by the theme, not by new chrome | failing-check `DismissIcon` in `DangerBrush #FB7185` reads warm pink — the ✕ silhouette carries severity (E1) |
| Atelier | the workshop's job board | `WarningIcon` fetch-failure amber vs copper accent — icon-led, never amber text alone (the standing Atelier rule) |
| Loom Aurora | indigo ledger | `AccentSelection` focus fill is faint on `#161930` — the rail disambiguates (standing Aurora rule); attention-rail kind icons at `TextMuted` must clear 3:1 on `SurfacePanel` — they do at 14 px |

### 3.10 Tokens & classes

`Border.Card` × 2 on `SurfaceWindow`, 8 px transparent gutter · rows on `SurfaceHoverGhost` rest →
`SurfaceHover` hover, selection `AccentSelection` + 3 px `AccentBrush` rail (reserved column) ·
`Border.RefChip` branch pills · `ArrowUpIcon`/`ArrowDownIcon` 10 `TextMuted` + counts ·
`Button.IconButton` (fetch/pull) + small `Button.Primary` Open · `Button.Accent` Add repository ·
`Button.Secondary` Locate…/Open Accounts · category headers `4,4` padding + `BorderHairline` ·
attention icons `DismissIcon` (`DangerBrush`) / `PullRequestIcon` / `IssueIcon` (`TextMuted`) at
14 · skeleton blocks `SurfaceCard` radius 8 · type: name Body 13/600, meta Label 11 `TextMuted`,
paths/SHAs `TextBlock.Mono` · ES per the standard ramp.

---

## 4 · Split into branches — the untangling wizard (P2-C4)

*The end-of-day tangle: one working tree, three unrelated changes. The wizard deals every hunk
into named piles and commits each pile to its own branch — with arithmetic proof that nothing was
lost.*

### 4.1 Contract, verified

P2-C4 item 1: cluster uncommitted changes by path/hunk (T-06 `PatchParser`/`PatchBuilder`) into N
proposed groups; the user adjusts; each group commits to its own new branch via sequential
apply/commit/reset cycles — **journaled (T-19), tree-snapshot-protected (P2-37)**, and explicitly
*not* a persistent virtual-branch mode. The binding invariant: **the wizard never loses a hunk**
(sum of groups == original diff, property-tested). Item 2 (stacked restacking) is designed
compactly in §4.11 — it lives on already-designed surfaces.

### 4.2 Information architecture

A dedicated window — `SplitBranchesWindow` (+ `SplitBranchesViewModel`), the
`ConflictResolverWindow` precedent: a real working session deserves its own frame, not a modal
squeezed over the cockpit. Entry: the staging panel's composer-options overflow
(`Split into branches…`), the repo actions menu, and the palette (`ActionIds.SplitIntoBranches`).
The entry is disabled on a clean tree with the TT-2 tooltip
`Nothing to split — the working tree is clean`.

Three horizontal bands:

```
1  the piles    left rail 320: group list — each future branch, plus the "Stays put" tray
2  the evidence center: the selected group's files & hunks, rendered by the shipped diff rows
3  the ledger   footer strip: the conservation count + [Split into N branches]
```

- **A group** = a radius-8 `SurfaceCard` in the rail: an editable branch-name field
  (`TextBlock.Mono` content — it *is* a ref, N-6; validated live against ref rules and existing
  branches), then its file rows (file-type glyph `TextMuted` + name + hunk count `3 hunks`).
  The initial proposal clusters by top-level path and pre-names groups from the dominant
  directory (`services`, `docs`) — a *suggestion* to react to, which beats a blank form
  (the clustering is Core's; the design contract is only that proposals arrive pre-named and
  every hunk starts somewhere).
- **The "Stays put" tray** is the first, unnamed group: hunks left here **remain in the working
  tree, untouched**. This reframes the invariant as a feature — the user splits out the two clean
  stories and leaves the mess for later; nothing is forced into a branch.
- **The evidence band** reuses the diff viewer's row anatomy verbatim (SurfaceDesigns §3 —
  number gutters, `DiffAddedBg`/`DiffRemovedBg`, hunk bars), read-only, with one addition per
  hunk bar: the **assignment chip** — a radius-999 chip naming the hunk's current group
  (`services`, or `Stays put` in `TextMuted`). Click it (or press a digit) to reassign; the chip
  text swaps with a 130 ms fade (G-6), nothing moves (E3: the chip slot is fixed-width).
- **The conservation ledger** (footer, fixed height): the running arithmetic —
  `23 hunks — 18 into 3 branches, 5 staying put.` — recomputed live. This line is the property
  test rendered as UI: the sum is always visible, always exact.
- **Base picker**: one row above the footer — `Branching from: main` (the current HEAD, a
  `Border.RefChip`) with a change flyout. Every group branches from the same base in v1 (matching
  the sequential apply/commit/reset contract).

### 4.3 Primary flow

1. Open → the proposal paints composed: N pre-named groups, every hunk assigned (most to
   `Stays put` when clustering confidence is low — honest defaults over confident guesses, V-6).
2. Walk the evidence: `↓`/`↑` through hunks; each hunk's diff is right there — assignment is an
   act of *reading*, not memory.
3. Deal: press `1`–`9` to send the focused hunk to that group, `0` for `Stays put`; or click the
   assignment chip. `N` creates a group; `F2` renames the focused group's branch.
4. Watch the ledger: `5 staying put` is a decision, not a leftover — the wizard never nags about
   it.
5. `Ctrl+Enter` → the confirmation (§4.4) → execution with honest progress → the landing (§4.7).

### 4.4 The confirmation & execution

Not a destructive dialog — the operation is snapshot-protected and journaled, and the copy says
exactly that (C-2/C-5):

> **Title:** `Split changes into 3 branches?`
> **Body:** `Each group commits to its own new branch off main, and the working tree keeps only
> what stays put. GitLoom snapshots the tree first and journals the whole split — Undo restores
> everything.`
> `Button.Primary` **Split** · `Button.Secondary` **Cancel**.

(The `Button.Primary` verb follows the `Stash and switch` precedent from Microcopy §2 — a safe,
recoverable act never wears `Button.Danger`, and no `Button.Accent` sits on a confirmation.)

Execution overlay (scrim + radius-12 `SurfacePanel` card): a monotonic bar (G-8) with phase-named
status — `Committing services — 2 of 3` — and no cancel mid-cycle (the honest statement: each
group is an atomic apply/commit/reset; the card says `Finishing the current group…` if closing is
requested). On completion, the window closes and the toast lands:
`Split 18 hunks into 3 branches.` with **Undo** (T-2 — the journal makes it genuinely reversible;
the toast is the promise, C-5).

### 4.5 Keyboard-first

| Key | Action |
|---|---|
| `↓`/`↑` | next / previous hunk (evidence band) |
| `[` / `]` | previous / next file |
| `1`–`9` | assign the focused hunk to group 1–9 (the rail numbers its groups) |
| `0` | send to `Stays put` |
| `N` | new group (focus lands in its name field) |
| `F2` | rename the focused group |
| `Ctrl+Enter` | Split (opens the confirmation; disabled until ≥ 1 named, non-empty group) |
| `Esc` | close the wizard — assignments are kept for the session, nothing has run |

### 4.6 Mockup

```
┌ SplitBranchesWindow ──────────────────────────────────────────────────────────┐
│ Split into branches                    Branching from: (main)                 │
│ ┌ rail 320 ──────────────┐ ┌ evidence ─────────────────────────────────────┐ │
│ │ Stays put · 5 hunks    │ │ ▤ src/Services/ GitServices.cs   +42 −7       │ │
│ │ ────────────────────── │ │ @@ 118–126 ····················  (1 services) │ │
│ │ 1 ┌ services ────────┐ │ │ 118 118 │  context                            │ │
│ │   │ 2 files · 9 hunks│ │ │ 119     │− removed                            │ │
│ │   │ ▤ GitServices.cs │ │ │     119 │+ added                              │ │
│ │   └──────────────────┘ │ │ @@ 240–261 ····················  (0 stays put)│ │
│ │ 2 ┌ fix-blame-cache ─┐ │ │▐240     │− old call     ← focused hunk        │ │
│ │   │ 1 file · 4 hunks │ │ │     240 │+ new call                           │ │
│ │   └──────────────────┘ │ └───────────────────────────────────────────────┘ │
│ │ 3 ┌ docs-pass ───────┐ │                                                   │
│ │   │ 2 files · 5 hunks│ │                                                   │
│ │   └──────────────────┘ │                                                   │
│ └────────────────────────┘                                                   │
│ 23 hunks — 18 into 3 branches, 5 staying put.        [Split into 3 branches] │
└───────────────────────────────────────────────────────────────────────────────┘
▐ = focused hunk rail · (n group) = the assignment chip on each hunk bar
```

### 4.7 States

- **Empty**: unreachable — the entry point is disabled on a clean tree (§4.2). Inside the wizard,
  a group emptied of its last hunk stays in the rail (its name is work the user did) with
  `0 hunks — won't create a branch` in `TextMuted`; the Split button's count excludes it.
- **Loading** (initial clustering on a large diff): the rail shows two `SurfaceCard` skeleton
  group blocks and the evidence band keeps the shipped diff-loading behavior (previous content
  dimmed 0.6, spinner glyph — SurfaceDesigns §3.5); one cross-fade when the proposal lands.
- **Name errors**: inline under the field, the shipped validation pattern —
  `A branch named services already exists. Pick another name.` (`DangerBrush` on the message
  only); the Split button disables while any *non-empty* group's name is invalid, and its TT-2
  tooltip names the first offender.
- **Execution error** (a group's apply/commit fails mid-run): the overlay stops with pattern E —
  `The split stopped at fix-blame-cache — {plain one-line reason}. The snapshot restored your
  working tree, and the 1 branch already created was removed — everything is as it was.` +
  `Button.Secondary` "Close". All-or-nothing is the only honest contract a "never loses a hunk"
  feature can offer; partial success would strand the user mid-arithmetic.
- **Concurrent change** (the watcher sees the tree change under the open wizard): a quiet
  `WarningIcon` strip above the footer — `The working tree changed since this proposal was made.
  Refresh to re-cluster — your group names are kept.` + `Button.Primary` "Refresh". Never
  auto-refresh: the user's assignments are work (M-2's respect for state, applied to data).

### 4.8 The delight moment

**The arithmetic closes.** The conservation ledger ticks with every keystroke —
`5 staying put` → `2` → `0` — and when the last hunk is dealt it reads, in full:
`23 hunks — 23 into 3 branches, 0 staying put.` No badge, no glow: the number *is* the
reassurance, the property test made visible. The second beat is the landing toast with **Undo**
attached — a tree-rewriting operation that arrives with its own way back in the same pill (the
product promise in one line, C-5/V-5).

### 4.9 The signature accent

**The receiving group.** Exactly one group in the rail is *active* — the one that `Enter`-clicks
and the most recent digit assignments target — and it carries the accent treatment
(`AccentSelection` header fill + the 3 px rail). The assignment chips on hunk bars stay neutral
(`SurfaceCard` fill, `TextPrimary` label; `TextMuted` for `Stays put`) so the eye tracks *where
things are going* by the one lit pile, not by a rainbow of group colors — five accent-colored
groups would be the identical-card-grid failure in miniature. The footer's Split button is
`Button.Primary`, not Accent: the view's accent is the active group; the way forward is the
ledger reaching a shape the user likes.

### 4.10 Across the five themes

| Theme | The wizard reads as | Watch-item |
|---|---|---|
| Midnight Loom | charcoal sorting bench, one violet-lit pile | reference |
| Daylight Loom | paper worksheet | group cards are `SurfaceCard #FFFFFF` on `SurfacePanel #F7F8FB` — the hairline carries the card edge (standing Daylight rule); diff tints per SurfaceDesigns §3.7 |
| Command Deck | tactical triage board | assignment chips must stay neutral fills — a teal-tinted chip would read as the accent and break the one-lit-pile model |
| Atelier | the craftsman's sorting tray — the theme this feature was named for | `WarningBrush` concurrent-change strip vs copper accent: icon-led (standing rule) |
| Loom Aurora | indigo bench | `AccentSelection` on the active group header is faint over `#161930` — the rail carries it (standing rule); diff remove-tint plum per SurfaceDesigns §3.7 |

### 4.11 Stacked branches — the sibling gesture (P2-C4 item 2), compact

Stacking rides surfaces already designed; it adds one relation and its maintenance, not a new
view:

- **Mark**: branch context menu → `Stack on…` → pick the base. The relation renders on the
  graph's ref chip as a text suffix — `feature · stacked on main` inside the chip's existing
  label (E4: text, not a glyph-only mark; no new icon geometry is minted for v1). The tooltip
  carries the consequence: `When main moves, GitLoom offers to restack feature onto it` (TT-1).
- **Restack**: when the base moves (merge/amend), a panel strip appears in the timeline card —
  `main moved — feature is stacked on it. Restack replays feature's 3 commits onto the new tip.`
  + `Button.Primary` "Restack feature" · `Button.Secondary` "Not now". Never automatic and never
  a toast: replaying commits is a decision (T-3). Success lands the rebase-shaped toast:
  `Restacked feature onto main. 3 commits replayed.` (the Microcopy §4 rebase form). Conflicts
  route to the shipped resolver with the §1.4 rebase-conflict copy verbatim; the restack is
  journaled and undoable (C-5).
- **Reading**: no lane recolor, no new graph paint — the stack is chip text + the strip. The
  weave already shows the topology; the design refuses to duplicate it in a second channel.

### 4.12 Tokens & classes

Window on `SurfaceWindow`, bands split by 8 px gutters · group cards `SurfaceCard` radius 8 +
`BorderHairline`; active group `AccentSelection` header + 3 px `AccentBrush` rail · branch-name
fields radius 8 `SurfaceCard`, focus `AccentBrush` (global style), content `TextBlock.Mono` ·
assignment chips radius-999 `SurfaceCard` fill, `TextPrimary`/`TextMuted` label, 130 ms content
fade (G-6) · evidence band per SurfaceDesigns §3.8 (number gutters, `DiffAddedBg`/`DiffRemovedBg`,
hunk bars) · footer ledger Body 12 `TextPrimary`, counts invariant · `Button.Primary` Split/
Restack/Refresh · `Button.Secondary` Cancel/Close/Not now · confirmation per pattern C shape ·
execution overlay: scrim `#C0000000`, radius-12 `SurfacePanel` card, `AccentBrush` monotonic bar
(G-8) · toast pill per SurfaceDesigns §1.5 with Undo · `WarningIcon` 12 on the concurrent-change
strip.

---

## 5 · The client polish pack (P2-C5)

*Seven parity checkboxes — Tower, Fork, Sublime Merge, GitKraken — each landing inside an
already-designed surface. The pack's design discipline: add capability, add zero new accents, add
no new surface language.*

### 5.1 The pack at a glance

| # | Item | Host surface | The experience in one line |
|---|---|---|---|
| 1 | Standalone mergetool | `ConflictResolverWindow`, solo | `git mergetool` opens our 3-pane resolver; saving resolves, exit codes tell git the truth |
| 2 | External difftool hand-off | diff header `…` overflow | `Open in Beyond Compare` — a validated launch, never a shell string |
| 3 | Partial stash | staging ledger | `Stash checked files…` — the checkbox model the user already speaks |
| 4 | Patch files & WIP share | commit/selection context menus | drag out a `.patch`; push a share ref with a copyable import path |
| 5 | Templates & gitmoji | `CommitComposerView` options | templates prefill; gitmoji is opt-in user content, never product chrome |
| 6 | Diff text search | diff card, `Ctrl+F` | an inline find strip with honest counts — `3 of 17` |
| 7 | AI commit message (BYOK) | `CommitComposerView` | a draft that names its provenance and never commits itself |

### 5.2 Standalone mergetool (item 1)

**Flow.** `gitloom mergetool <local> <base> <remote> <merged>` (P2-32 CLI) launches the shipped
`ConflictResolverWindow` alone — no shell, no navbar, no repo context: the window's caption line
(the SurfaceDesigns §3.2 pattern) shows the merged file's path in `TextBlock.Mono`. The resolver
behaves identically to its in-app life (engine-driven off `IMergeDiffService`, accept-chevrons,
lock-step scrolling) — that identity is the point: the terminal user meets GitLoom's best surface
with zero adoption cost.

**Exit honesty (V-6).** `Button.Accent` **Save and finish** writes the merged file and exits 0 —
git proceeds. Closing the window unresolved asks once:
Title `Leave the merge unresolved?` · body `Git will still see this file as conflicted — run the
mergetool again or resolve it another way. Nothing you tried here is written.` ·
`Button.Primary` "Leave unresolved" · `Button.Secondary` "Keep resolving". Exit 1 — the truth,
never a fake success code. Bad argv prints one plain stderr line (`gitloom mergetool needs four
paths: local base remote merged`) and exits 2 — the CLI speaks the same voice at the same economy.

**States.** Loading: the three panes paint composed from the given files (M-2). Error (a path
unreadable): pattern E inline in the window, `Button.Secondary` "Close" (exit 2). The delight is
the *frame*: a terminal command that opens a native 60 fps resolver and gets a correct exit code
back — the trojan-horse moment named in the Master Doc.

### 5.3 External difftool hand-off (item 2)

Preferences gains a **Diff tools** list (tool name + an `ArgumentList` template with
`$LOCAL`/`$REMOTE` placeholders — launched via `ProcessStartInfo.ArgumentList` per the
`SshKeyService` precedent, never a concatenated shell string). The diff header's existing `…`
overflow (SurfaceDesigns §3.2) gains `Open in <tool>` — named per V-1, never a generic
"External tool". Disabled with TT-2 when no tool is configured:
`Add a diff tool in Preferences to open comparisons outside GitLoom`. No new chrome on the diff
surface itself.

### 5.4 Partial stash (item 3)

The staging ledger's checkbox model already answers "which files" — partial stash simply reuses
it: a `Stash checked files…` item in the ledger's context menu and the Stash segment's toolbar,
enabled when 1 ≤ checked < all (a full stash stays the plain Stash path). The dialog: the standard
`Stash message…` field + one `Include untracked` checkbox → `Button.Primary` **Stash checked
files**. The toast is Microcopy §4 verbatim: `Stashed 4 files. Restore them from the Stash list.`
No new states — the stash tab already owns the aftermath.

### 5.5 Patch files & WIP sharing (item 4)

- **Save as patch**: commit context menu `Save as patch…` (file-save dialog, default
  `<sha7>-<slug>.patch`) — and the commit row is a drag source: dragging it out of the window
  produces the `.patch` (the T-09b drag vocabulary, pointed outward). Toast:
  `Saved a1b2c3d as a patch.` Multi-select saves a series.
- **Apply patch**: repo actions menu `Apply patch…`; a failed apply is pattern E with the
  no-partial-change guarantee (`git apply` semantics):
  `The patch doesn't apply to this tree — {plain one-line reason}. Nothing was changed.`
- **Share as patch ref**: context menu `Share as patch ref…` → a small dialog (radius-12
  `SurfacePanel`): one sentence — `This pushes your work-in-progress to
  refs/gitloom/patches/9f8e7d6 on origin — visible to anyone with fetch access, outside normal
  branches.` — then, after the push, the same dialog shows the fetch command in a mono read-only
  field with a copy `Button.IconButton`. The copyable command lives in the **dialog**, not the
  toast — T-2 permits only Undo/Dismiss in a pill, and a share you must paste somewhere deserves
  a surface that waits. Import: `Apply patch…` accepts a patch ref name and fetches it.

### 5.6 Commit templates & gitmoji (item 5)

- **Templates**: the composer options overflow (`⋯`, where signing now lives — SurfaceDesigns
  §4.2) gains `Commit template` — auto-detects a repo `commit.template`/`.gitmessage`, plus saved
  app-level templates. An active template prefills the message box; the watermark names it
  (`Template: conventional-scoped`) so prefilled ≠ typed is always legible (V-6).
- **Gitmoji**: an opt-in picker in the Conventional composer's Type row — a searchable flyout of
  `glyph + name + meaning` rows (`✨ feat — a new feature`). **The V-3 line**: emoji in the
  *user's commit message* is user content, permitted; GitLoom's own chrome, labels, and toasts
  never render one. The picker is off by default and lives behind the options overflow —
  discoverable, never pushed.

### 5.7 Diff text search (item 6)

`Ctrl+F` with the diff focused drops a **find strip** under the diff header (fixed height, so the
hunks don't reflow — E3): `TextBox.searchBox` (watermark `Find in diff`) · match count Label 11
`3 of 17` · `ChevronDownIcon`/`ChevronRightIcon`-family prev/next `Button.IconButton`s · `Esc`
closes. `Enter`/`Shift+Enter` step next/previous, wrapping with the count as the honest odometer.

**The highlight collision, resolved.** The diff's signature accent is already the line-selection
paint (SurfaceDesigns §3.6) — search may not borrow it. Matches therefore render as
`SurfaceHover` fill spans (a neutral lift off the line ground); only the **current** match adds a
1 px `AccentBrush` outline. Currentness is triple-carried: the outline, the `3 of 17` count, and
the viewport scrolling to it (E1 — no channel is alone). Zero matches: the count slot reads
`No matches` in `TextMuted`; the strip never turns red — an absent string is a fact, not an error
(V-2).

### 5.8 AI commit message, BYOK (item 7)

A `Draft message` ghost button (`Button.IconButton` + label) in the composer's options row —
present only when a provider key exists (P2-01 keys); otherwise the option renders disabled with
TT-2: `Add an API key in Preferences to draft a message from the staged diff`. On invoke: the
button shows the spinning glyph (`Drafting…`, M-6); the result fills the message box
**fully selected** — one keystroke replaces it, zero keystrokes keeps it — with one Label line
beneath: `Drafted from the staged diff — edit or commit as yours.` (V-6 provenance; the line
disappears at the first edit, because then it *is* yours). The draft is run through the same
T-31 convention validation as typed text; it never auto-commits, and the T-30 scan still gates
the landing. Explicitly a parity checkbox (Master Doc), styled like one: no sparkle iconography,
no accent, no "AI" branding in the chrome — the feature is a quiet verb.

### 5.9 The pack's delight, accent, and five-theme reading

- **The delight moment**: invisibility. Six of seven items add capability with zero new visual
  language; the one nameable beat is §5.2's — a terminal `git mergetool` resolving in a native
  60 fps three-pane editor and handing git back a correct exit code. Restraint is the feature
  (M-5).
- **The signature accent**: none added. Every item inherits its host surface's accent
  (the resolver's existing accent; the composer's Commit button; the diff's selection paint) —
  the One-Accent Rule holds *because* the pack declines to decorate.
- **Across the five themes**: every component above is an existing classed control on existing
  surfaces, already gated by SurfaceDesigns §§3.7/4.7 — the only new watch-item is the find
  strip's `SurfaceHover` match fill, which must stay distinguishable from the hover state of the
  row under the pointer; the current-match `AccentBrush` outline and the count carry it (checked:
  the fill never appears on a full row, only on a text span, so shape separates them in all five
  themes).
- **Tokens & classes**: `TextBox.searchBox` · `Button.IconButton`/`Primary`/`Secondary`/`Accent`
  (Save and finish, §5.2 only) · `SurfaceHover` match spans + 1 px `AccentBrush` current outline ·
  `TextBlock.Mono` for paths/refs/commands · dialogs radius-12 `SurfacePanel` + hairline + scrim ·
  toast pills per the standard form · all counts invariant-formatted.

---

## Appendix A — New-strings inventory (five-question gate)

Every user-facing string introduced by this document, run through the Voice Bible Appendix A gate
(object · way back · audit-log tone · word economy · severity-by-role). Microcopy.md remains the
final-strings authority — at implementation these rows graduate into it; strings quoted from it
in the sections above are not repeated here.

| # | Surface | String | Pattern |
|---|---|---|---|
| A1 | Bisect setup | `96 commits between good and bad — about 7 steps.` | status line (V-1, V-6 — "about" keeps the estimate honest) |
| A2 | Bisect setup, dirty tree | `Bisect checks out a different commit at each step, and 3 files have uncommitted changes. Commit or stash them first.` | E (V-5 canonical shape) |
| A3 | Bisect setup, bad ref | `No commit found for "v1.o" — check the tag or paste a SHA.` | E, inline field |
| A4 | Bisect strip | `Step 3 of ~7 · 12 commits remain · testing a1b2c3d — "wire up settings"` | status line |
| A5 | Bisect strip, done | `Found the first bad commit.` | status line (T-1 tense) |
| A6 | Bisect checkout error | `This step couldn't check out a1b2c3d — {reason}. Fix it and retry, or stop the bisect; stopping returns HEAD to main.` | E (E-3 both exits) |
| A7 | Bisect, skip-exhausted | `Bisect can't narrow further — the 3 remaining candidates were all skipped. The first bad commit is one of them.` | E/status (V-6) |
| A8 | Stop-bisect tooltip | `Returns HEAD to main and forgets the session — nothing else changes` | TT-1 |
| A9 | Bisect end toast | `Bisect ended. HEAD returned to main.` | T (T-1) |
| A10 | Search watermark | `Search commits, branches, tags, files, PRs, issues` | label (V-1) |
| A11 | Search, empty query | `Type to search — Enter jumps to the top result.` | hint |
| A12 | Search, host not connected | `Sign in to GitHub to include PRs and issues.` | ES-3 line |
| A13 | Search, host failed | `PRs and issues couldn't load — local results are complete.` | E (V-6) |
| A14 | Repositories ES | `No repositories yet` / `Add a local repository or clone one to see its status here.` | ES-1/ES-2 |
| A15 | Repo row, missing folder | `Folder not found at ~/code/react` | status (V-1) |
| A16 | Repo row, fetch failed | `Fetch failed — check Remotes` (full pattern-E string in tooltip) | E-compact |
| A17 | Remove-repo confirm | `Remove react from this list?` / `This only removes the bookmark — the repository on disk isn't touched.` | C (C-1, C-2) |
| A18 | Attention rail, not connected | `Nothing to gather yet` / `Connect GitHub to see review requests, assigned issues, and failing checks across your repositories.` | ES-3 |
| A19 | Attention rail, all clear | `Nothing needs attention` / `No review requests, assigned issues, or failing checks right now.` | ES-4 |
| A20 | Split entry tooltip | `Nothing to split — the working tree is clean` | TT-2 |
| A21 | Split ledger | `23 hunks — 18 into 3 branches, 5 staying put.` | status (V-1) |
| A22 | Split confirm | `Split changes into 3 branches?` / `Each group commits to its own new branch off main, and the working tree keeps only what stays put. GitLoom snapshots the tree first and journals the whole split — Undo restores everything.` | C (C-2, C-5) |
| A23 | Split toast | `Split 18 hunks into 3 branches.` + Undo | T (T-1, T-2, C-5) |
| A24 | Split execution error | `The split stopped at fix-blame-cache — {reason}. The snapshot restored your working tree, and the 1 branch already created was removed — everything is as it was.` | E (E-3, V-6) |
| A25 | Split, tree changed | `The working tree changed since this proposal was made. Refresh to re-cluster — your group names are kept.` | E/notice |
| A26 | Restack strip | `main moved — feature is stacked on it. Restack replays feature's 3 commits onto the new tip.` | notice (V-1) |
| A27 | Restack toast | `Restacked feature onto main. 3 commits replayed.` | T (mirrors the §4 rebase form) |
| A28 | Stacked-chip tooltip | `When main moves, GitLoom offers to restack feature onto it` | TT-1 |
| A29 | Mergetool leave confirm | `Leave the merge unresolved?` / `Git will still see this file as conflicted — run the mergetool again or resolve it another way. Nothing you tried here is written.` | C-shape (non-destructive: Primary) |
| A30 | Mergetool CLI usage | `gitloom mergetool needs four paths: local base remote merged` | CLI stderr |
| A31 | Difftool disabled tooltip | `Add a diff tool in Preferences to open comparisons outside GitLoom` | TT-2 |
| A32 | Patch apply error | `The patch doesn't apply to this tree — {reason}. Nothing was changed.` | E (the no-partial-change guarantee) |
| A33 | Patch save toast | `Saved a1b2c3d as a patch.` | T (T-1) |
| A34 | Share-ref dialog | `This pushes your work-in-progress to refs/gitloom/patches/9f8e7d6 on origin — visible to anyone with fetch access, outside normal branches.` | C-2-style consequence statement |
| A35 | Find-in-diff | `Find in diff` (watermark) · `3 of 17` · `No matches` | labels |
| A36 | AI draft disabled tooltip | `Add an API key in Preferences to draft a message from the staged diff` | TT-2 |
| A37 | AI draft provenance | `Drafted from the staged diff — edit or commit as yours.` | label (V-6) |
| A38 | Pull disabled tooltip (repo row) | `Pull needs an upstream — this branch tracks no remote` | TT-2 |
| A39 | Bisect Start tooltip | `Enter a good and a bad commit — a tag or an older commit works for good` | TT-2 |

## Appendix B — Keyboard additions (for the `ShortcutMap` / `ActionRegistry`)

New global gestures (rebindable via T-18, conflict-checked by `ShortcutMap.Conflicts()`):
`Ctrl+Shift+F` → `ActionIds.SearchEverything` · `Ctrl+F` → `ActionIds.FindInDiff` (diff-focused).
New palette actions (no default gesture): `StartBisect`, `StopBisect`, `SplitIntoBranches`,
`AddRepository`, `FetchAllRepositories`, `OpenRepository:<name>` (one per registered repo).
Scoped (non-`ShortcutMap`) keys: bisect `G`/`B`/`S` (timeline-focused, session active); wizard
`1`–`9`/`0`/`N`/`F2`/`[`/`]`/`Ctrl+Enter`; Repositories home `F`/`P`/`Enter`/`Tab`. Every scoped
key has a visible on-surface equivalent (buttons, chips, menus) — keyboard-first, never
keyboard-only (TT-4's principle).

## Appendix C — Self-gate

| Gate | Result |
|---|---|
| Conforms to DesignSystem G1–G5 / E1–E4 | Pass — no lane or semantic hue invented; every new state pairing carries a non-color channel (bisect verdict glyphs + labels, dimming + counts, search spans' 600 weight, ahead/behind glyph+number, find-strip outline + count, assignment-chip text); reserved slots throughout (badge slot, session strip, find strip, chip slots — E3); every status chip/pill carries text (E4) |
| Builds on SurfaceDesigns, doesn't redo it | Pass — reuses the driver/focus contract (§1.6, §2.3), the diff anatomy (§4.2, §5.7), the ES card, the toast form, the segment/chip vocabulary; Part 1 sections are cited, never restated |
| Strings: Microcopy reused where it exists; new strings gated | Pass — detached pill, stale-fetch tooltip, stash toast, rebase-conflict copy, palette no-match pair all quoted verbatim; 39 new strings inventoried in Appendix A, each through the five-question gate |
| One signature accent per view, named | Pass — the candidate spotlight (§1.9), the match spans (§2.7), the focused thread (§3.8), the receiving group (§4.9), and the pack's deliberate none (§5.9) |
| Empty/loading/error per feature | Pass — §§1.7, 2.5, 3.6, 4.7, and per-item in §5 |
| Keyboard-first per feature | Pass — §§1.4, 2.4, 3.4, 4.5, Appendix B; every key has a visible equivalent |
| Delight moment per feature, inside the motion budget | Pass — the narrowing weave (§1.8), the lit thread (§2.6), the loom at rest (§3.7), the arithmetic closing (§4.8), invisibility/the mergetool frame (§5.9); all motion is the 120–150 ms fade family (G-2…G-8), no bounce, no layout animation |
| Five-theme reading per feature | Pass — §§1.10, 2.8, 3.9, 4.10, 5.9, with real token values and the standing proximity risks (Command Deck teal≈Success, Atelier copper≈Warning, Daylight faint tint, Aurora faint selection) re-applied where each feature meets them |
| Scales respected | Pass — every radius ∈ {6,8,12,999}, spacing ∈ {4,5,8,10,15,20}, type on the DESIGN.md ramp; row heights (28/32) are layout, not scale tokens |
| Anti-references held | Pass — the sketch's "card grid" is redesigned as a ledger (§3.1); no hero metrics, no eyebrows, no mascots, no sparkle-AI branding (§5.8); modals used only where a decision or a session demands one |
| No [Horizon] UI designed | Pass — §3's swarm future is a named seam for Lane E; bisect's agent-driving seam is Core's, not a UI |
| Voice held (V-1…V-8) | Pass — no "please/sorry/we", no exclamation marks, sentence case, contractions in refusals, severity by role; verdict buttons justified against C-3 in §1.3 |
