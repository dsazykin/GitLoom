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
- Put business logic in `GitLoom.Core` behind an interface; keep `GitLoom.App` thin.
- Verify with `dotnet build` (and `dotnet test` for Core changes) before declaring done. Report failures with output — don't paper over them.
- **Do not commit or push.** Make the edits, then hand back a detailed proposed commit message and let the human commit. Never touch `main` directly.
- Don't invent features from the roadmap into the code unless asked; the docs are forward-looking.
