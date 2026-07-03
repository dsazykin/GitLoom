# AGENTS.md

Guidance for humans and AI coding agents working in the GitLoom repository. Read this before making changes.

## What GitLoom Is

GitLoom is a premium, natively-rendered Git GUI (Avalonia + `LibGit2Sharp`) evolving into a multi-agent control center for orchestrating swarms of autonomous coding CLIs. **Today the codebase is a working Git client**; the swarm/sandbox/terminal features described in the roadmap are planned, not yet built. Keep that distinction in mind — the planning docs are the *destination*, the code is the *current state*.

- **README.md** — product overview, current vs. planned features.
- **GitLoom_Roadmap.md**, **Implementation_Plan.md** — deep architecture and phasing (aspirational).
- **Team_Structure.md**, **Team_Intake_Form.md** — pod split and ownership seams for the scaling team.
- **GitLoom_Git_Audit_And_Roadmap.md**, **GitLoom_Market_Research*.md** — supporting analysis.

If you change how the app actually works, update **README.md**; if you change the plan, update the roadmap/plan docs — don't let them drift.

## Tech Stack

- **.NET 10** (SDK pinned to `10.0.100` via `global.json`, `latestFeature` roll-forward). C# with `Nullable` enabled everywhere.
- **UI:** Avalonia 11.1.3, Fluent theme, `AvaloniaEdit` (text/diff), `LiveChartsCore` (analytics), compiled bindings on by default.
- **MVVM:** `CommunityToolkit.Mvvm` — use `[ObservableProperty]` and `[RelayCommand]`, not hand-written `INotifyPropertyChanged`.
- **Git engine:** `LibGit2Sharp` 0.30.0 (native libgit2 handles — see the handle rule below).
- **Persistence:** SQLite via EF Core (`Microsoft.EntityFrameworkCore.Sqlite`), migrations applied on startup.
- **Secrets/keys:** `.env` via `DotNetEnv`; OS keyring / `AspNetCore.DataProtection` via `Security/SecureKeyring.cs`.
- **Tests:** xUnit + `coverlet`.

## Solution Layout

`GitLoom.slnx` (not a `.sln`) is the solution and includes three projects:

- **`GitLoom.Core`** — all business logic, git operations, models, EF `AppDbContext`, analytics, commit-graph routing, security. No UI dependency. Prefer putting logic here.
  - `Services/` — interface-first (`IGitService`/`GitServices`, `IMergeDiffService`, `ISettingsService`, `RepositoryWatcher`). Add an interface for anything a ViewModel consumes.
  - `Models/`, `Analytics/`, `Graph/`, `Migrations/`, `Security/`, `Sync/`.
- **`GitLoom.App`** — Avalonia desktop UI. `ViewModels/` ↔ `Views/` paired by convention (wired through `ViewLocator.cs`); also `Controls/`, `Converters/`. Entry point `Program.cs`, app bootstrap `App.axaml.cs`.
- **`GitLoom.Tests`** — xUnit tests for Core.

Not in the solution (scratch/experiments, don't rely on them): `GitLoom.StyleConsole`, `GitLoom.StyleTests`, `GitLoom.AvaloniaTests`.

## Repository Map — Where Things Live

Keep this map current: **whenever you add, move, or delete a file, update the entry here** (see the AI-agent rule at the bottom). It is the index that lets an agent find code without re-scanning the tree.

### `GitLoom.Core/` (business logic, no UI)

- **`AppDbContext.cs`** — EF Core `DbContext`; the SQLite schema (repositories, categories, user preferences). Migrations live in `Migrations/`.
- **`Services/`** — the service layer every ViewModel talks to. Interface-first:
  - `IGitService.cs` / `GitServices.cs` — the core git engine. **All** LibGit2Sharp access goes through `GitServices.ExecuteWithRepo(...)`. Commit, stage, branch, merge, rebase, stash, cherry-pick, reset, diff, history.
  - `ISettingsService.cs` / `SettingsService.cs` — user preferences + workspace/category persistence via `AppDbContext`.
  - `RepositoryWatcher.cs` — `FileSystemWatcher` wrapper that raises change events so the UI can refresh.
  - `IInteractiveRebaseService.cs` / `InteractiveRebaseService.cs` — interactive rebase sequence controller.
- **`Models/`** — plain data/domain types: `Repository`, `WorkspaceCategory`, `GitCommitItem`, `GitBranchItem`, `GitFileStatus`, `GitStashItem`, `GitDiffLine`, `SideBySideDiffRows`, `GitHubRepository`, `CommitSearchFilter`, `UserPreferences`, `PullStrategy`, `HostKind`, `RebaseTodoItem`.
- **`Graph/`** — commit-graph layout: `CommitGraphRouter.cs` (lane assignment / edge routing) + `GraphModels.cs` (nodes/edges/lanes). Consumed by the `CommitGraphCanvas` control.
- **`Analytics/`** — `RepositoryAnalyzer.cs`, `LanguageRegistry.cs`/`LanguageModel.cs` (language breakdown), `PunchCardStats.cs`. Feeds `AnalyticsView`.
- **`Security/`** — `SecureKeyring.cs` (OS keyring / DataProtection secret storage), `GitHostDetector.cs` + `Models/HostKind.cs` (classify a remote as GitHub/GitLab/etc.).
- **`Sync/`** — `GitHubAuthClient.cs` (GitHub device-flow OAuth, remote repo listing).
- **`Exceptions/`** — the typed exception hierarchy (`GitLoomException` base; `AuthenticationRequiredException`, `MergeConflictException`, `GitOperationException`, `SshAuthenticationException`, `RemoteNotFoundException`, `GitIdentityMissingException`). Throw these from Core; catch in ViewModels to drive dialogs.
- **`Migrations/`** — generated EF migrations + `AppDbContextModelSnapshot.cs`. Never hand-edit an applied one.
- Scratch/placeholder (ignore, safe to delete): `Class1.cs`, `Services/Test.cs`.

### `GitLoom.App/` (Avalonia UI)

- **`Program.cs`** — entry point. **`App.axaml` / `App.axaml.cs`** — app bootstrap, DB migrate-on-startup, static `Settings`, and the **global resource dictionary + styles** (the design-system source of truth — see the UI section).
- **`ViewLocator.cs`** — maps a `FooViewModel` to its `FooView` by naming convention. New VM/View pairs are wired automatically as long as they follow the name pattern.
- **`Views/`** — one `.axaml` (+ `.axaml.cs`) per screen/dialog. Paired 1:1 with `ViewModels/`:
  - Shell: `MainWindow` (top nav, sidebar, overlays: command palette / delete-confirm / invalid-repo).
  - Repo workspace: `RepoDashboardView` (layout host) → `StagingPanelView`, `DiffViewerView`, `CommitTimelineView`.
  - Feature screens: `CloneDashboardView`, `AnalyticsView`.
  - Dialogs/windows: `CreateBranchDialog`, `ConfirmationDialog`, `CheckoutConflictDialog`, `MergeCommitDialog`, `ConflictedFilesWindow`, `ConflictResolverWindow`, `DeviceFlowAuthDialog`, `InteractiveRebaseWindow`.
- **`ViewModels/`** — one per view above, plus row/item VMs with no view of their own: `CommitRowViewModel`, `MenuItemViewModel`, `BranchBrowserViewModel`, `InteractiveRebaseViewModel`. All derive from `ViewModelBase.cs`.
- **`Controls/`** — custom-drawn controls. `CommitGraphCanvas.cs` renders the commit graph (uses `Core/Graph`).
- **`Converters/`** — `IValueConverter`s: `FileExtensionToIconConverter`, `BoolToOpacityConverter`.

### Tests & tooling

- **`GitLoom.Tests/`** — xUnit tests for Core (`GitServicesTests`, `CommitGraphRouterTests`, `SettingsServiceTests`, `AppDbContextTests`, `GitHostDetectorTests`).
- **`.github/workflows/ci.yml`** — CI. **`Dockerfile` / `docker-compose.yml` / `.dockerignore`** — container build. **`global.json`** — SDK pin. **`.config/dotnet-tools.json`** — local tools (`dotnet-ef`).

## Build, Test, Run

Run from the repo root:

```bash
dotnet restore
dotnet build                    # builds the whole solution — do this after any change
dotnet test                     # runs GitLoom.Tests (xUnit)
dotnet run --project GitLoom.App   # launch the app
```

**Always run `dotnet build` after making changes**, and `dotnet test` when you touch Core.

### EF Core migrations

`dotnet-ef` is a local tool (`dotnet-tools.json`). The DB is created/migrated automatically on app startup (`App.axaml.cs`). When you change entities in `AppDbContext`:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project GitLoom.Core
```

Commit the generated migration + snapshot together. Never hand-edit an applied migration.

## Conventions

- **LibGit2Sharp handles:** always go through `IGitService.ExecuteWithRepo(...)`. It opens/disposes the native `Repository` handle deterministically. Do not hold long-lived `Repository` instances or new one up ad hoc — leaked native handles cause `.git/index.lock` collisions, which is exactly the class of bug this app exists to prevent.
- **MVVM:** ViewModels derive from `ViewModelBase`; expose state with `[ObservableProperty]`, actions with `[RelayCommand]` (async commands as `...Async`). Keep git/IO work in Core services, off the UI thread; marshal back with `Dispatcher.UIThread` when updating bound state.
- **Views:** one `.axaml` + `.axaml.cs` per ViewModel, resolved via `ViewLocator`. Prefer compiled bindings (`x:DataType`).
- **DI:** there is currently **no DI container** — `App` exposes a static `Settings`, and `MainWindowViewModel` is instantiated directly. Follow the existing pattern; if you introduce a container, do it deliberately and update this file.
- **Nullability:** `<Nullable>enable</Nullable>` is on — don't suppress warnings to make something compile; fix the nullability.
- **Tests:** xUnit `[Fact]`/`[Theory]`, naming `Method_ShouldExpectedBehavior_Condition` (see `GitServicesTests.cs`). Tests that touch a real repo create a temp repo and clean it up in `Dispose`.

## UI / Design System

GitLoom has **one theme: a Classic IDE Dark look** (VS Code family). Every screen must read as the same app. The single source of truth is the resource dictionary and styles in **`App.axaml`** — treat it like a design-token file. All the tokens, brushes, icons, and control styles described below are defined there.

### The golden rule: no raw colors

**Never hardcode a hex color (`#RRGGBB`, `"White"`, `"Black"`) in a View or control.** Always bind a named brush from `App.axaml`:

```xml
Foreground="{StaticResource TextPrimary}"   <!-- yes -->
Foreground="#CCCCCC"                          <!-- no  -->
```

If you need a shade that has no token, **add the token to `App.axaml`** (and to the table below) rather than inlining it. The same applies to code-behind/controls: **resolve brushes from application resources instead of `SolidColorBrush.Parse(...)`-ing palette values.** `CommitGraphCanvas` is the reference pattern — it reads the `Branch*` lane brushes from `Application.Current` with literal fallbacks, so there's still one source of truth.

### Color tokens (defined in `App.axaml`)

| Purpose | Resource key | Value |
|---|---|---|
| Window background | `BgWindow` | `#1E1E1E` |
| Sidebar / panel-header background | `BgSidebar` | `#252526` |
| Deepest surface (code/diff editor) | `BgObsidian` | `#181818` |
| Input / card / raised-panel surface | `PanelSurface` | `#2D2D30` |
| Subtle border / divider / splitter | `BorderSubtle` | `#3E3E42` |
| Neutral button background | `ButtonBg` | `#333337` |
| Button hover / selection | `ButtonHover` | `#3E3E42` |
| Primary text (incl. titles) | `TextPrimary` | `#CCCCCC` |
| Muted / secondary text, hints | `TextMuted` | `#858585` |
| Accent / links / current branch | `BranchCyan` | `#569CD6` |
| Success / added / "ours" | `BranchGreen` | `#6A9955` |
| Danger / removed / destructive | `BranchRed` | `#F44747` |
| Graph lane | `BranchPink` | `#C586C0` |
| Graph lane | `BranchYellow` | `#DCDCAA` |
| Accent-button hover shade | `AccentHover` | `#6BA8DE` |
| Added diff-line background | `DiffAddedBg` | `#1A3A22` |
| Removed diff-line background | `DiffRemovedBg` | `#4D1D22` |

Semantics: **cyan = accent/primary action & current branch**, **green = success/added**, **red = danger/destructive/removed**, **muted = metadata/hints**. Use them by meaning, not by hue preference. There is **no** separate "white"/"standard" text token — titles and body both use `TextPrimary` (don't reintroduce `TextStandard`/`TextWhite`).

### Icons

Icons are shared `StreamGeometry` resources in `App.axaml`, rendered with `<PathIcon Data="{StaticResource SomeIcon}" .../>`. The set includes `Checkmark`, `Search`, `ChevronRight`/`ChevronDown`, `Play`, `Refresh`, `Eye`, `Cloud`, `Folder`, `ArrowUp`/`ArrowDown`, `Warning`, `Menu`, `Undo`, `DocumentAdd`, `Dismiss`, `Document`, `Lock`, and `GitHub`. Standard sizes: **14×14** toolbar/inline actions, **10–12** chevrons/adornments, **18** nav, **48–64** empty-state art. Add new icons to `App.axaml` and reference by key — **never paste raw path data inline.** Muted actions use `Foreground="{StaticResource TextMuted}"`.

### Buttons — pick a class, never inline the color

`App.axaml` defines the whole button system; choose by role (fill and on-fill text are baked in — don't override them):

| Class | Use for | Fill / text |
|---|---|---|
| `Button.Primary` | neutral / default actions ("Commit and Push…", "Modify", "New Branch from Current") | `ButtonBg`, `TextPrimary`, subtle border |
| `Button.Accent` | the **one** emphasized CTA per view (Commit, Create, Clone, Login, Accept Changes) | `BranchCyan` fill, **black** text |
| `Button.Success` | positive / confirming action (Stash, Accept Incoming, Commit & Push) | `BranchGreen` fill, **black** text |
| `Button.Danger` | destructive action (Delete, Discard) | `BranchRed` fill, **white** text |
| `Button.Secondary` | cancel / dismiss | transparent, `TextMuted`, subtle border |

Rules: **at most one `Accent` per view**; use `Danger` for anything destructive (not ad-hoc reds like `#e02a2a`/`#F44336`); cancels are `Secondary`. Don't set `Background`/`Foreground` on a classed button.

### Checkboxes — sized to their label

The global `CheckBox` style renders at **scale 0.85** (left-anchored, so layout doesn't shift) to match header/option text. `CheckBox.FileRow` renders at **0.65** for the smaller per-item rows (the staging file lists). Use the class — **don't** add inline `RenderTransform="scale(...)"` on checkboxes.

### Layout & spacing

- **Windows/dialogs:** `Background="{StaticResource BgWindow}"`, content padding `20–30`, `WindowStartupLocation="CenterOwner"` (or `CenterScreen` for `MainWindow`). Be consistent about `ExtendClientAreaToDecorationsHint` — match sibling dialogs.
- **Panels:** header/toolbar rows use `BgSidebar` + a bottom `BorderSubtle`; input/card surfaces use `PanelSurface`; splitters are `Width/Height="4"` with `BorderSubtle` (or `Transparent`) background.
- **Overlays** (command palette, confirmations): full-bleed scrim `#C0000000`, centered card on `BgSidebar` with `BorderSubtle`, `CornerRadius="8"`, and a soft `BoxShadow`.
- **Spacing scale:** prefer `4 / 5 / 8 / 10 / 15 / 20` for margins, padding, and `Spacing`. **Font sizes:** `11` (metadata), `12–13` (body/controls), `14` (emphasis), `16–18` (titles), `24` (hero). Don't introduce off-scale one-offs.
- **Corner radius:** `4` for controls/buttons, `8` for cards/overlays.

### Reuse over copy-paste

Shared visual behaviors live **once** in `App.axaml` `Application.Styles` and are referenced by class — the button and checkbox classes above, the `PathIcon.spinning` animation, and the `PathIcon.Chevron`/`.expanded` data-swap. Don't redefine any of these per view.

### Allowed literal-color exceptions

A few literals are intentional and are **not** theme chrome — leave them, don't "tokenize" them: semi-transparent black scrims/shadows (`#C0000000`, `#40000000`, `#80000000`, …), the repo icon **color-picker swatches** in `MainWindow`/`MainWindowViewModel` (those literals *are* the user-selectable colors), and the semantic conflict-block tints returned by `ConflictResolverWindowViewModel`.

### Before you finish a UI change

Skim the sibling views for the same element (button, dialog, list row, toolbar) and match them. If you find yourself typing a hex value, a raw path geometry, an inline checkbox scale, or a padding not on the scale above, stop and use/define the token or class instead. New tokens, icons, and classes go in `App.axaml` and are documented in this section.

## Git Hygiene

- **Line endings are normalized to LF** in-repo via `.gitattributes`. Don't fight it or re-commit whole files as "modified" due to CRLF. Windows-only scripts (`.bat`/`.cmd`/`.ps1`) stay CRLF by rule.
- **Never commit:** `.env`, `*.db`/SQLite/WAL files, `bin/`/`obj/`, IDE folders, or agent session files (`.agents/`, `.antigravitycli/`, `.session_map.json`, `.cortex_plan.md`) — all already in `.gitignore`.
- Secrets live in `.env` (see `.env.example`, e.g. `GITHUB_CLIENT_ID`). Never hardcode credentials or paste them into committed files/docs.
- Commit messages follow the existing `type: summary` style (`feat:`, `fix:`, `ui:`, `docs:`).

### Branching & commits (mandatory)

- **No direct pushes to `main`.** `main` is protected. Every change lands via a Pull Request.
- **One branch per feature/fix.** Branch off the latest `main` (e.g. `feat/agent-executor`, `fix/index-lock`), open a PR, get it reviewed, and merge only when complete and green.
- **Agents must not commit or push.** An AI agent makes the code changes and then **generates a detailed proposed commit message** for the human to review and commit themselves. The message should follow the `type: summary` convention with a body explaining *what changed and why* (not just what). The human owner is responsible for staging, committing, and opening the PR.

## For AI Agents Specifically

- Make the smallest change that satisfies the request; match surrounding style rather than reformatting files.
- **Keep the repo index current.** Whenever you **create, move, rename, or delete a file**, update the **Repository Map** section above in the same change so the entire repo stays indexed. Add the new file under the right heading with a one-line description of what it holds; remove entries for files you delete. A new file without a map entry is an incomplete change.
- When you add or change UI, follow the **UI / Design System** section: no hardcoded colors, reuse `App.axaml` tokens/styles, and add any new token/icon there (and to its table) rather than inlining it.
- Put business logic in `GitLoom.Core` behind an interface; keep `GitLoom.App` thin.
- Verify with `dotnet build` (and `dotnet test` for Core changes) before declaring done. Report failures with output — don't paper over them.
- **Do not commit or push.** Make the edits, then hand back a detailed proposed commit message and let the human commit. Never touch `main` directly.
- Don't invent features from the roadmap into the code unless asked; the docs are forward-looking.
