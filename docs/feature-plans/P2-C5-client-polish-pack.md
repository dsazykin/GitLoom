# P2-C5 — Client Polish Pack — Implementation Plan

**Task ID:** P2-C5 · **Track:** client parity · **Priority:** P2 (Tower/Fork/Sublime/GitKraken
parity checkboxes — each item small).
**Depends on:** shipped surfaces; item 1 needs the P2-32 CLI verb infrastructure.
**Branch:** implement on `feature/P2-C5-client-polish-pack` off `phase2`; PR targets `phase2`.
**PR granularity:** one PR each or grouped sensibly — **the task ID covers the set**; every PR
links P2-C5 and names its item number(s).

> **Source of truth:** §P2-C5 of `docs/GitLoom_Master_Implementation_Document_v2.md`. Seven
> parity items; none is a differentiator by design (item 7 explicitly revises the earlier "skip
> AI commit messages" ruling — buyers screen for it).

---

## 0. Context — what you can rely on

| Fact | Where |
|---|---|
| T-04 3-pane conflict resolver (openable against arbitrary files) | conflict resolver stack |
| Validated launcher rules (ArgumentList, no shell) | `ExternalEditorLauncher` (P2-40) / launcher hardening |
| Stash backend incl. paths + untracked (T-07-era work) | `IGitService` stash surface |
| `RunGitChecked` for `format-patch`/`am`; authenticated push for refs | `GitServices.cs` |
| T-31 conventional-commit composer | `CommitComposerViewModel` + Core builder |
| Diff viewer (T-13) | `DiffViewerViewModel` |
| BYOK keys + gateway-style budgeted calls | P2-01 (`llm_*`), `ApiKeyHealthService` pattern |
| CLI verb host (`gitloom …`) | P2-32 SDK/CLI surface |

---

## 1. The seven items — contract + implementation

### Item 1 — Standalone mergetool mode

- **Contract:** `gitloom mergetool <local> <base> <remote> <merged>` opens the shipped T-04
  3-pane resolver on those files; exit code 0 on save-and-resolve, nonzero on abort;
  registerable as `git mergetool` (documented `git config` snippet + a "Register as git
  mergetool" settings button writing the user's gitconfig **with consent**).
- **Impl:** CLI verb (P2-32 host) parses the four paths → launches the app in a slim
  resolver-only window mode (`--mergetool` app switch) → resolver saves to `<merged>`.
- **Files:** `GitLoom.App/Program.cs` (switch), `MergetoolWindow.axaml(.cs)` +
  `MergetoolViewModel.cs`, CLI verb registration.

### Item 2 — External diff/merge tool hand-off

- **Contract:** configurable tool templates (`{local}/{remote}/{base}/{merged}` placeholders),
  ArgumentList construction, validated-launcher rules; menu entries in diff views and the
  conflict flow.
- **Impl:** extend the P2-40 `ExternalEditorLauncher` template model with a tool-kind dimension
  (difftool/mergetool) — **one template engine, not two**.

### Item 3 — Partial stash UI

- **Contract:** multi-select files in the staging panel → "Stash selected"
  (`git stash push -- <paths>`), include-untracked toggle. Backend shipped — UI only.
- **Files:** `StagingPanelViewModel` (+ context menu), stash list refresh.

### Item 4 — Patch files & WIP sharing

- **Contract:** `format-patch`/`am` wrappers (typed, CLI runner); drag-out `.patch` from a
  commit; "Share as patch ref" pushes `refs/gitloom/patches/<id>` to the existing remote +
  an import flow (list/fetch/apply patch refs) — the Git-native 80% of GitKraken Cloud Patches,
  no hosted service.
- **Impl:** `PatchExchangeService` (`CreatePatch(sha range) → file`, `ApplyPatch(file)` via `am`
  with a 3-way fallback, `SharePatchRef`, `ListPatchRefs`, `ImportPatchRef`); ref pushes through
  the authenticated CLI path; drag-out via Avalonia `DataObject` file drag.

### Item 5 — Commit templates + gitmoji picker

- **Contract:** folded into the T-31 composer: per-repo commit template (`commit.template`
  respected + app-managed templates) and a gitmoji picker (static curated list, prepends the
  emoji/`:code:` per config).
- **Files:** `CommitComposerViewModel` extension + a small picker flyout; templates persisted in
  `AppDbContext`.

### Item 6 — Diff text search

- **Contract:** Ctrl+F overlay in diff views — match count, next/prev, highlight in both panes;
  **verify absence first** (the master doc flags it — grep the diff viewer for an existing
  search before building).
- **Files:** `DiffViewerViewModel` search state + overlay control; matcher over the rendered
  line model (pure helper).

### Item 7 — AI commit message (BYOK checkbox)

- **Contract:** one prompt over the **staged diff** via P2-01 keys (`llm_*`) into the T-31
  composer, with convention enforcement (the generated message passes the conventional
  builder's validation; non-conforming output is repaired locally by re-prefixing, not
  re-prompted). Checkbox off by default; disabled without a key.
- **Impl:** `CommitMessageSuggester` (HttpMessageHandler seam, provider from stored keys,
  bounded diff excerpt, single call — no gateway dependency client-side; budget note in
  settings). Key never logged (reuse `RedactionExtensions`).

---

## 2. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| mergetool invoked with a missing file path | typed usage error, nonzero exit, no window |
| tool template with hostile paths | ArgumentList verbatim; no shell parsing |
| partial stash with untracked files, toggle off/on | paths honored; untracked included only when toggled |
| `am` conflict on apply | 3-way fallback then typed conflict routing (T-04) |
| patch-ref import of a nonexistent id | typed failure |
| gitmoji + template + convention combined | composer output stays convention-valid |
| AI suggestion over an empty staged diff | affordance disabled |
| AI provider 401 | typed inline error, key-scrubbed |

---

## 3. Invariants (MUST)

1. Item 2 reuses the single template/launcher engine (no second shell-escape path).
2. Patch-ref sharing uses only the existing authenticated remote plumbing — no new transport.
3. AI messages always pass conventional validation before landing in the composer.
4. Registering as `git mergetool` edits user gitconfig only with explicit consent, reversibly.

---

## 4. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Mergetool_ArgPlumbing_ResolverRoundTrip` | CLI verb → resolver → saved `<merged>` content + exit codes |
| 2 | `ToolTemplate_ArgConstruction` | hostile-path matrix → exact ArgumentList |
| 3 | `PartialStash_PathsAndUntracked` | selected paths stashed; untracked toggle semantics |
| 4 | `Patch_RoundTrip_IncludingRefShare` | format-patch → am on a second fixture clone; share ref → import → identical commits (bare-remote fixture) |
| 5 | `Composer_TemplateGitmojiSnapshot` | template + gitmoji output snapshot; convention-valid |
| 6 | `DiffSearch_MatchesAndNavigation` | fixture diff → count/next/prev/highlight spans |
| 7 | `AiMessage_ConventionEnforced` | fixture provider response (non-conforming) → repaired to valid; 401 → scrubbed typed error |

---

## 5. Rejection triggers / Reviewer script

**Rejection:** a second launcher/template engine; a hosted-service dependency for patch sharing;
AI message bypassing convention validation; unconsented gitconfig writes.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Mergetool|FullyQualifiedName~ToolTemplate|FullyQualifiedName~PartialStash|FullyQualifiedName~Patch|FullyQualifiedName~DiffSearch|FullyQualifiedName~AiMessage"
grep -rn "llm_" GitLoom.App/ | grep -i "log\|console"   # 0 hits
```

---

## 6. Definition of done

- [ ] All seven items shipped (one PR each or sensible groups, every PR linking P2-C5 + item numbers).
- [ ] Edge matrix + invariants covered per item; reviewer script clean per PR.
- [ ] `AGENTS.md` Repository Map updated with every new file, per PR.
