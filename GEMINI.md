# GEMINI.md

This file provides guidance to Gemini (and the Gemini CLI) when working with code in this repository.

## Read AGENTS.md first

**[`AGENTS.md`](AGENTS.md) is the source of truth** for architecture, the design system, conventions, and git hygiene, and it is kept current with the code. Read it before changing anything. This file only adds operational specifics and highlights a few rules you must not miss. Where the two ever disagree, AGENTS.md wins — and fix the drift.

## What this is

A working native Git GUI (**.NET 10**, Avalonia 11 + MVVM via `CommunityToolkit.Mvvm`, `LibGit2Sharp`, SQLite/EF Core). The multi-agent / sandbox / terminal features in `README.md` and the roadmap docs are **planned, not built** — don't implement them into the code unless asked. The docs are the destination; the code is the current state.

Solution is `GitLoom.slnx` (a `.slnx`, not `.sln`). Three projects matter: **`GitLoom.Core`** (all logic, interface-first services, no UI — put logic here), **`GitLoom.App`** (thin Avalonia UI, `ViewModels/` ↔ `Views/` paired via `ViewLocator`), **`GitLoom.Tests`** (xUnit). `GitLoom.StyleConsole`, `GitLoom.StyleTests`, `GitLoom.AvaloniaTests` are scratch — not in the solution, don't rely on them.

## Commands

```bash
dotnet restore
dotnet build                         # build whole solution — run after any change
dotnet test                          # all xUnit tests (run when you touch Core)
dotnet run --project GitLoom.App     # launch the app

# a single test class, or one method by name
dotnet test --filter "FullyQualifiedName~CommitGraphRouterTests"
dotnet test --filter "FullyQualifiedName~GitServicesTests&Name=<MethodName>"
```

The SDK is pinned to `10.0.100` (`global.json`, `latestFeature` roll-forward) so `dotnet` picks the right toolchain automatically.

Docker wrappers reproduce the exact toolchain for **build/test/EF only** (not the GUI): `docker compose run --rm build|test|shell`.

### EF Core migrations

`dotnet-ef` is a local tool; the DB migrates on app startup (`App.axaml.cs`). After changing entities in `AppDbContext`:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project GitLoom.Core
```

Commit the migration + snapshot together; never hand-edit an applied migration.

### Build gotcha: close the app first

`dotnet build` fails with `MSB3021 … apphost.exe … being used by another process` if a `GitLoom.App` instance is still running — it holds a lock on the output exe. That error is a lock, not a code error (XAML/C# already compiled). Close the running app and rebuild.

## Non-negotiable rules (details in AGENTS.md)

- **LibGit2Sharp only through `IGitService.ExecuteWithRepo(...)`** — it opens/disposes the native handle deterministically. Ad-hoc or long-lived `Repository` handles leak and cause `.git/index.lock` collisions (the exact bug this app exists to prevent).
- **No raw colors in UI.** Bind design tokens with `{DynamicResource …}` (never `StaticResource` for colors — it won't follow live theme switches). Pick a `Button.*` / `Border.*` component class by role instead of setting `Background`/`Foreground`. New tokens go in **every** `Themes/*.axaml`; new classes/icons go in `App.axaml`. There is one design system with five switchable color themes — never assume "dark" (Daylight Loom is light).
- **Keep the Repository Map in AGENTS.md current.** When you create/move/rename/delete a file, update its entry in the same change — an unindexed file is an incomplete change.
- **No DI container** currently: `App` exposes a static `Settings`; `MainWindowViewModel` is constructed directly. Follow the pattern.
- **Do not commit or push, and never touch `main` directly.** Make the edits, verify with `dotnet build` (+ `dotnet test` for Core), then hand back a detailed proposed commit message (`type: summary` convention) for the human to commit via PR.
