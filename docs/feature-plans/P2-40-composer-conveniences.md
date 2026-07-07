# P2-40 — Composer & Review Conveniences: Image Input, Voice Dictation, Edit-in-Place, External-Editor Links, Rendered Previews — Implementation Plan

**Task ID:** P2-40 · **Milestone:** M7.75 · **Priority:** P2-parity (Codex/Kepler/Jules input
breadth; Superset editor pattern).
**Depends on:** P2-03 (composer/terminal), T-13 (viewers).
**Branch:** implement on `feature/P2-40-composer-conveniences` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-40 of `docs/GitLoom_Master_Implementation_Document_v2.md`. Five small
> conveniences, one task. Edit-in-place is deliberately **not an IDE** — small review-time fixes
> only.

---

## 0. Context — what exists today

The agent composer is text-only; reviewers can't make a one-line fix without leaving the app;
markdown/mermaid render as raw text. Competitors ship all five conveniences.

### What you can rely on

| Fact | Where |
|---|---|
| Composer (terminal input path) + adapter message shape | P2-03/P2-14 |
| Sandbox mount layout (`/workspace`, per-agent) | P2-07 |
| Staging service (auto-stage after save) | `StagingPanelViewModel` + Core staging surface |
| Validated launcher (ArgumentList, scheme checks) | `BrowserLauncher`/launcher rules (f10e627 hardening) |
| T-18 rebindable shortcuts | `ShortcutSettingsViewModel` |
| File/diff viewers (T-13) | `DiffViewerViewModel`, file view stack |
| Headless render harness | TI-00 |

New dependencies (App): `AvaloniaEdit` (edit-in-place), `Markdown.Avalonia` or equivalent +
a Mermaid renderer (render-only; if a WebView is required, reuse `LivePreviewControl`'s wrapped
dependency from P2-33 — no second WebView integration).

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/ComposerAttachmentService.cs` (image → sandbox mount file → path reference) |
| **Create** | `GitLoom.App/Services/VoiceDictationService.cs` (Windows speech; BYOK Whisper fallback via P2-01 keys) |
| **Create** | `GitLoom.App/Controls/EditInPlaceControl.cs` (AvaloniaEdit behind an "Edit" toggle; save + auto-stage) |
| **Create** | `GitLoom.Core/Services/ExternalEditorLauncher.cs` (command templates, ArgumentList construction) + settings model |
| **Create** | `GitLoom.App/Controls/RenderedPreviewControl.cs` (Markdown + Mermaid, render-only) |
| **Edit** | composer view (attach button/paste/drag, push-to-talk), file/diff viewer (Edit toggle, preview toggle, "Open in …" menu) |
| **Create** | `GitLoom.Tests/ComposerAttachmentTests.cs`, `EditInPlaceTests.cs`, `ExternalEditorTemplateTests.cs`, `RenderedPreviewSmokeTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

1. **Image input:** paste/drag into the composer → file lands **inside the agent's sandbox
   mount** → path reference in the adapter message.
2. **Voice dictation:** Windows-native speech (`Windows.Media.SpeechRecognition`) or BYOK Whisper
   into the composer; push-to-talk keybinding (T-18 rebindable).
3. **Edit-in-place:** AvaloniaEdit behind an "Edit" toggle on the file/diff view — save +
   auto-stage.
4. **External editor deep links:** "Open in VS Code / JetBrains / …" per-file and per-worktree —
   configurable command templates through the validated launcher pattern.
5. **Rendered previews:** render-only Markdown + Mermaid in the file/diff viewer.

---

## 3. Implementation steps

1. **Attachments:** clipboard/drag image → PNG normalized → written via the daemon to
   `<worktree>/.gitloom-attachments/<id>.png` (inside the sandbox mount — the **only** landing
   zone, invariant 1; the folder is gitignored via the worktree's exclude file) → composer
   inserts the container-relative path into the adapter message. Size cap + type allowlist
   (png/jpg/gif). Cleanup with agent teardown.
2. **Dictation:** `VoiceDictationService` — Windows speech recognition session when available
   (WinRT projection; feature-detect, degrade gracefully on non-Windows/dev-loop); alternative:
   BYOK Whisper (`llm_openai` key present → audio capture → transcription endpoint, gateway-
   budgeted). Push-to-talk: registered T-18 action with a default chord; text streams into the
   composer caret position.
3. **Edit-in-place:** viewer toolbar "Edit" toggle swaps the read view for AvaloniaEdit on the
   working-tree file (worktree files only — never blob views of historical revisions; the toggle
   is absent there). Save → write + auto-stage (existing staging service) + refresh the diff.
   Dirty-close prompts. No project/solution features — a single-file editor by design.
4. **External editors:** settings-defined templates
   (`code --goto {file}:{line}`, `rider {worktree}`) with `{file}`/`{line}`/`{worktree}`
   placeholders; construction via `ProcessStartInfo.ArgumentList` — **never string
   interpolation into a shell** (invariant 2); unknown placeholders → typed validation at save.
   Menu entries per-file (diff context menu) and per-worktree (agent card / repo menu).
5. **Previews:** `RenderedPreviewControl` — markdown via the chosen Avalonia renderer; mermaid
   blocks rendered read-only (JS-based renderer inside the P2-33 wrapped WebView when available;
   otherwise a "preview unavailable" placeholder — never a second WebView stack). Toggle on
   `.md` files in the file/diff viewer; render-only (no editing surface).

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| pasted image over the size cap | typed refusal, nothing written |
| attachment on a torn-down agent | typed failure; no orphan files |
| edit toggle on a historical blob view | toggle absent/disabled |
| editor template with `{file}` containing spaces/quotes | ArgumentList passes it verbatim; no shell parsing |
| dictation on a machine without speech support and no Whisper key | affordance hidden/disabled with reason |
| malformed mermaid block | placeholder error rendering, viewer stable |

---

## 5. Invariants (MUST)

1. Pasted images land **only** inside the agent's sandbox mount.
2. Editor templates never shell-interpolate untrusted paths (ArgumentList, launcher rules).
3. Previews are render-only; edit-in-place touches working-tree files only.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Attachment_PathPlumbing` | paste fixture → file under the worktree attachments dir; message contains container-relative path; teardown removes it |
| 2 | `Attachment_CapAndTypes` | oversize/wrong type → typed refusal |
| 3 | `EditSaveStage_RoundTrip` | edit → save → file content + staged state + diff refresh |
| 4 | `EditToggle_HistoricalBlobAbsent` | blob view → no edit affordance |
| 5 | `EditorTemplate_ArgConstruction` | templates with hostile paths (`a b"; rm`) → exact ArgumentList entries, no shell |
| 6 | `Preview_MarkdownMermaidSmoke` (headless) | fixture md renders; malformed mermaid → placeholder |
| 7 | `Dictation_FeatureDetect` | unsupported environment → disabled with reason (seam-mocked) |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** attachments outside the sandbox mount; shell-string editor launches; an editing
surface in previews; a second WebView integration; IDE feature creep in edit-in-place.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~ComposerAttachment|FullyQualifiedName~EditInPlace|FullyQualifiedName~ExternalEditorTemplate|FullyQualifiedName~RenderedPreview"
grep -rn "UseShellExecute = true" GitLoom.Core/Services/ExternalEditorLauncher.cs   # 0 hits
```

---

## 8. Definition of done

- [ ] Attachments (cap/type/teardown) into the sandbox mount with path references.
- [ ] Dictation (native + BYOK fallback) with rebindable push-to-talk; graceful degrade.
- [ ] Edit-in-place (save + auto-stage, worktree-only); external-editor templates via ArgumentList.
- [ ] Markdown/Mermaid render-only previews without a second WebView stack.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-40**, base `phase2`.
