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

- **`AppDbContext.cs`** — EF Core `DbContext`; the SQLite schema (repositories, categories, user preferences, pinned graph refs). Migrations live in `Migrations/`.
- **`Services/`** — the service layer every ViewModel talks to. Interface-first:
  - `IGitService.cs` / `GitServices.cs` — the core git engine. **All** LibGit2Sharp access goes through `GitServices.ExecuteWithRepo(...)`. Commit, stage, branch, tag, merge, rebase, stash, cherry-pick, reset, diff, history. Remotes management (T-10): CRUD (`GetRemotes`/`AddRemote`/`RemoveRemote`/`RenameRemote`/`SetRemoteUrl`), the `ResolveRemoteName`/`GetDefaultRemoteName` resolver that replaced every hardcoded `"origin"` (tracked → origin → sole remote → typed `RemoteNotFoundException`), a remote-named `Fetch` overload, and the three CLI push options (`PushForceWithLease` — lease only, never bare `--force`; `PushTags`; `PushSetUpstream`). Blame (T-11): `GetBlame(repoPath, path, startingSha?)` → per-line `BlameLine`s (1-based line numbers, typed `GitOperationException` on a path missing at the revision) computed via `ExecuteWithRepo`, plus `InvalidateBlameCache(repoPath)`. File history (T-12): `GetFileHistory` (rename-following newest-first log via `Commits.QueryBy(path)`, with a first-parent fallback walk so a file deleted at HEAD still shows its past), `GetFileAtCommit` (blob text; typed throw on a missing path or a binary blob), `GetFileDiffBetweenCommits` (adjacent-version patch = `git diff a b -- path`). Diff quality (T-13): the `GetFileDiff(...,bool ignoreWhitespace)` overload (CLI `git diff -w`, `--cached` when staged — whitespace-only changes collapse to zero hunks; partial staging is disabled by the caller in that mode) and `GetBlobBytesAtCommit` (raw blob bytes, no binary rejection — the "before" image source). Commit/tag signing (T-15): an optional `Func<UserPreferences>` ctor lets the app feed live signing prefs; when `SignCommits` is on `Commit`/`CreateTag` switch to the CLI (`git commit`/`git tag -s`) after writing `commit.gpgsign`/`tag.gpgsign`/`gpg.format`/`user.signingkey`/`gpg.program` to **local** repo config (`ApplySigningConfig`) — GIT_TERMINAL_PROMPT=0 keeps a bad key from hanging; `GetSignatureStatuses` batch-reads `%G?` via `SignatureStatusParser`; `ListSigningKeys` enumerates gpg secret keys / `~/.ssh/*.pub` for the picker.
  - `BlameCache.cs` — bounded LRU (~32 entries) keyed `(repoPath, path, headSha)` for T-11 blame results; invalidated per-repo on `RepositoryWatcher.RepositoryChanged`. Never unbounded (rejection trigger).
  - `AutoFetchService.cs` — background auto-fetch (T-10). One `PeriodicTimer` loop over the watched repo set fetches (prune) off the UI thread on the `UserPreferences.AutoFetchMinutes` cadence (0 = off); per-repo overlap guard, skip-while-operating, failures counted (`Fetched`/`FetchFailed` events, `GetLastFetched`). Concrete sealed class per the T-10 contract; no `DispatcherTimer` in Core (G-5). Cadence/clock are internal test seams (`IntervalOverride`/`Clock`) and `RunCycleAsync` runs one deterministic pass.
  - `IMergeDiffService.cs` / `MergeDiffService.cs` — pure 3-way merge chunker (strings in → ordered `MergeChunk`s out; no repo/IO). Consumed by the conflict-resolver UI (T-04).
  - `PatchParser.cs` / `PatchBuilder.cs` — pure unified-diff engine (T-06): parse/serialize (byte-identical round-trip) and build hunk/line subsets that feed the existing `StageHunk`/`UnstageHunk`/`DiscardHunk`. No repo/IO.
  - `WorktreePorcelainParser.cs` — pure parser for `git worktree list --porcelain` (T-07) → `WorktreeItem`s. Worktree ops are CLI-driven (libgit2 worktree API is a locked no).
  - `LineHistoryFilter.cs` — pure T-12 line-history filter: keeps the file revisions whose adjacent-version diff touches a line range (old- or new-side hunk overlap), reusing `PatchParser`. Documented as a `git log -L` approximation; no repo/IO.
  - `IntraLineDiff.cs` — pure T-13 intra-line (word-level) diff engine: given the old/new text of a changed line pair, returns the changed character sub-ranges per side via DiffPlex `WordChunker`. Surrogate-safe (span boundaries snap outward off surrogate-pair midpoints). No repo/IO/Avalonia; feeds `GitDiffLine.HighlightSpans`.
  - `WhitespaceMarkers.cs` — pure T-13 trailing-whitespace detector: `TrailingWhitespace(line)` → the trailing-run `(Start,Length)` (whole line when all-whitespace, null when none). Feeds `GitDiffLine.TrailingWhitespaceSpan`.
  - `SignatureStatusParser.cs` — pure T-15 signing helper: maps git's `%G?` codes (G/B/U/X/Y/R/E/N) to `SignatureStatus` and parses batched `git log --format=%H|%G?|%GS` output into a SHA→`CommitSignatureInfo` map. No repo/IO; feeds the commit-timeline verification badges.
  - `ImageDiffDetection.cs` — pure T-13 image/binary helpers: `IsImageCandidate(path,isBinary)` (extension table {png,jpg,jpeg,gif,bmp,webp,ico}), `DiffIndicatesBinary`/`LooksBinary` (binary sniffing), `FormatBinarySummary(old,new)` (invariant-culture size summary). No repo/IO.
  - `ISettingsService.cs` / `SettingsService.cs` — user preferences + workspace/category persistence via `AppDbContext`.
  - `RepositoryWatcher.cs` — `FileSystemWatcher` wrapper that raises change events so the UI can refresh.
  - `IInteractiveRebaseService.cs` / `InteractiveRebaseService.cs` — interactive rebase sequence controller.
  - `IPinnedRefService.cs` / `PinnedRefService.cs` — per-repo pinned branches/tags (T-09), persisted via `AppDbContext`; pinned refs order first into the commit-graph router (left-most lanes).
- **`Models/`** — plain data/domain types: `Repository`, `WorkspaceCategory`, `GitCommitItem`, `GitBranchItem`, `GitFileStatus`, `GitStashItem`, `GitDiffLine`, `SideBySideDiffRows`, `GitHubRepository`, `CommitSearchFilter`, `UserPreferences`, `PullStrategy`, `HostKind`, `RebaseTodoItem`, `MergeChunk` (+ `ChunkKind`/`ChunkResolution` enums), `ConflictedFile`, `ConflictSide`, `GitTagItem`, `DiffLine`/`DiffHunk`/`FilePatch` (+ `DiffLineKind`; unified-diff model for partial staging, in `DiffHunk.cs`), `WorktreeItem`, `GitHeadState` (HEAD snapshot — attached/detached/unborn + tip SHA — driving the graph context-menu rules in T-09), `PinnedRef` (a pinned branch/tag per repo, ordered first into the graph router — T-09), `GitRemoteItem` (a configured remote: name + fetch URL + optional distinct push URL — T-10), `BlameLine` (per-line blame attribution — 1-based line number, commit SHA, author, date, boundary flag — T-11), `FileVersion` (one revision in a file's history — SHA/`ShortSha`, historical `PathAtCommit` that follows renames, short message, author, date — T-12), `SignatureStatus` (enum: the `%G?` verification states) + `CommitSignatureInfo` (status + signer) + `SigningKeyOption` (a pickable signing key — id/label) in `SignatureStatus.cs` (T-15). `UserPreferences` carries `AutoFetchMinutes` (T-10 auto-fetch cadence; 0 = off) and `SyntaxHighlightDiffs` (T-13 diff syntax-highlight toggle; default true). `UserPreferences` also carries the T-15 signing prefs (`SignCommits`, `GpgFormat`, `SigningKey`, `GpgProgram`, `ShowSignatureStatus`). `GitDiffLine` carries the T-13 intra-line `HighlightSpans` (changed-word char ranges into `Content`) + `TrailingWhitespaceSpan` + `EmphasisKey`.
- **`Graph/`** — commit-graph layout: `CommitGraphRouter.cs` (lane assignment / edge routing; accepts optional pinned-ref tip SHAs to reserve left-most lanes — T-09) + `GraphModels.cs` (nodes/edges/lanes). Consumed by the `CommitGraphCanvas` control.
- **`Analytics/`** — `RepositoryAnalyzer.cs`, `LanguageRegistry.cs`/`LanguageModel.cs` (language breakdown), `PunchCardStats.cs`. Feeds `AnalyticsView`.
- **`Security/`** — `SecureKeyring.cs` (OS keyring / DataProtection secret storage; T-14 added a storage-directory-override constructor for testability, `Retrieve` returns null on a corrupt/foreign payload), `GitHostDetector.cs` + `Models/HostKind.cs` (classify a remote as GitHub/GitLab/etc.; `UsernameForToken` is the **single source** for the host→token-username convention), `SshKeyService.cs` (T-14 SSH key manager: generate ed25519 via `ProcessStartInfo.ArgumentList` — never a shell string — list `~/.ssh` keys, copy public key, passphrase stored `sshpass_<sanitized-keypath>` in the keyring; the keygen `-N` passphrase is an argv element only because keygen is a *local* op, never on any network path), `CredentialResolver.cs` (T-14 single-source credentials picker: SSH-form remotes → `SshUserKeyCredentials` value object with key paths + keyring passphrase; token remotes → LibGit2Sharp `UsernamePasswordCredentials`; the pinned libgit2 build has no SSH transport, so SSH ops still run through the git CLI).
- **`Sync/`** — device-flow + multi-host auth (T-14): `DeviceFlowClient.cs` (reusable RFC-8628 OAuth device-flow engine + `DeviceFlowResponse`/`AccessTokenResponse`, injectable `HttpMessageHandler` seam), `GitHubAuthClient.cs` (thin GitHub facade over `DeviceFlowClient` preserving the Clone Dashboard API + authenticated repo listing), `IHostProvider.cs` (`IHostProvider` contract + `HostAuthContext` UI callbacks + `HostProviderBase` whose `TokenUsername` delegates to `GitHostDetector.UsernameForToken`), `GitHubProvider.cs`/`GitLabProvider.cs` (device-flow providers), `PatHostProviders.cs` (`PatHostProviderBase` + `BitbucketProvider`/`AzureDevOpsProvider`/`GenericHostProvider` — PAT-dialog v1), `HostProviderRegistry.cs` (`Resolve(host, HostKind)` → the right provider). Live device-flow/PAT/SSH round trips are deferred to the T-14 manual matrix (`// TODO(T-14 human-review)`).
- **`Exceptions/`** — the typed exception hierarchy (`GitLoomException` base; `AuthenticationRequiredException` — carries an optional `Host` (T-14) so the UI routes an unknown-host-no-token failure straight to that host's PAT dialog; `MergeConflictException`, `GitOperationException`, `SshAuthenticationException`, `RemoteNotFoundException`, `GitIdentityMissingException`). Throw these from Core; catch in ViewModels to drive dialogs.
- **`Migrations/`** — generated EF migrations + `AppDbContextModelSnapshot.cs`. Never hand-edit an applied one.
- Scratch/placeholder (ignore, safe to delete): `Class1.cs`, `Services/Test.cs`.

### `GitLoom.App/` (Avalonia UI)

- **`Program.cs`** — entry point. Also handles the interactive-rebase editor argv modes (`--rebase-editor` writes the todo list, `--rebase-msg` supplies the reword/squash message keyed by original SHA) — git launches the app as its own `GIT_SEQUENCE_EDITOR`/`GIT_EDITOR`, so this arg parsing runs *before* Avalonia starts; don't reorder it. **`App.axaml` / `App.axaml.cs`** — app bootstrap, DB migrate-on-startup, static `Settings`, theme initialization, and the **shared styles + icons + component classes** (see the UI section). Color tokens live in `Themes/`.
- **`Themes/`** — one `ResourceDictionary` per color theme (`MidnightLoom` default, `DaylightLoom`, `CommandDeck`, `Atelier`, `LoomAurora`), each defining the full token contract.
- **`Theming/ThemeManager.cs`** — runtime theme switching: swaps the merged theme dictionary, sets the theme variant, persists `UserPreferences.Theme`, raises `ThemeChanged`.
- **`ViewLocator.cs`** — maps a `FooViewModel` to its `FooView` by naming convention. New VM/View pairs are wired automatically as long as they follow the name pattern.
- **`Views/`** — one `.axaml` (+ `.axaml.cs`) per screen/dialog. Paired 1:1 with `ViewModels/`:
  - Shell: `MainWindow` (top nav, sidebar, overlays: command palette / delete-confirm / invalid-repo).
  - Repo workspace: `RepoDashboardView` (layout host) → `StagingPanelView`, `DiffViewerView`, `CommitTimelineView`.
  - Feature screens: `CloneDashboardView`, `AnalyticsView`, `BlameView` (T-11 blame: an `AvaloniaEdit` viewer with a `BlameGutterMargin` — age-heat bar + `author · shortSha · relative-date`, boundary shading, click-to-select-commit — defined in `BlameView.axaml.cs`; age-heat colors resolve from theme tokens at render time).
  - Dialogs/windows: `CreateBranchDialog`, `CreateTagDialog`, `ConfirmationDialog`, `CheckoutConflictDialog`, `MergeCommitDialog`, `ConflictedFilesWindow`, `ConflictResolverWindow`, `DeviceFlowAuthDialog`, `InteractiveRebaseWindow`, `RemotesWindow` (T-10 remotes manager: add / rename / edit-URL / remove), `AccountsWindow` (T-14 Accounts preferences: per-host rows with token status, device-flow / PAT sign-in, sign-out, add-custom-host), `SshKeysWindow` (T-14 SSH keys: list `~/.ssh` keys, generate ed25519 with optional passphrase, copy public key), `FileHistoryView` (T-12 file-history dialog: rename-following revision list + the selected-vs-predecessor diff, rendered read-only from `PatchParser`, plus the v1 line-history filter; a `Window` opened from the staging-panel and diff-viewer "History of this file" context menus; loads on open). `ConflictResolverWindow` is a synchronized 3-pane merge editor (Ours | Result | Theirs) on three `AvaloniaEdit` editors with filler-line alignment, lock-step scrolling, and `MergeBandRenderer` (region tints + gutter accept-chevrons), all in its code-behind; resolution logic stays in the ViewModel/engine.
- **`ViewModels/`** — one per view above, plus row/item VMs with no view of their own: `CommitRowViewModel` (carries the T-15 signature badge state — `SignatureStatus`/signer → verified/untrusted/bad derived flags + tooltip; badge holder collapses when unsigned), `MenuItemViewModel`, `BranchBrowserViewModel`, `InteractiveRebaseViewModel`, `MergeChunkViewModel` (one per merge chunk in the resolver), `ConflictedFileItem`, `DiffHunkRowViewModel`/`DiffLineRowViewModel` (partial-staging hunk/line rows in the diff viewer; carry T-13 intra-line `HighlightSpans`/`TrailingWhitespaceSpan`), `ImageDiffViewModel` (T-13 image-diff state: before/after `Bitmap`s + sizes + `SwipePosition`; swipe feel deferred), `RemotesViewModel`/`RemoteRowViewModel` (T-10 remotes-manager dialog + one editable row each), `AccountsViewModel`/`AccountRowViewModel` (T-14 Accounts page: per-host provider metadata + token status keyed `token_<host>`, PAT store/remove offline, device-flow sign-in wiring), `SshKeysViewModel`/`SshKeyRowViewModel` (T-14 SSH keys page: list/generate/copy over `SshKeyService`), `BlameViewModel` (T-11 blame: loads `GetBlame` off the UI thread on `Task.Run` with a `CancellationToken` cancelled on file switch — never a stale gutter — through `BlameCache`; click-a-line selects that commit via `WeakReferenceMessenger`), `FileHistoryViewModel` (T-12 file-history dialog: loads `GetFileHistory` off the UI thread and auto-selects the newest revision; the selection→predecessor diff recomputes off-thread with cancellation so rapid paging never renders a stale diff; the introducing revision renders as all-additions and binary blobs show a placeholder; the line-history filter narrows the list via `LineHistoryFilter`). All derive from `ViewModelBase.cs`. `RepoDashboardViewModel` owns the T-10 push-option commands (force-with-lease / set-upstream / push-tags), the `AutoFetchService` (Watch/Unwatch; surfaces the "last fetched N min ago" label with >15-min dimming), and is `IDisposable` — `MainWindowViewModel` disposes the outgoing workspace so the fetch loop + watcher don't leak. The conflict resolver (`ConflictResolverWindowViewModel` + `ConflictedFilesViewModel`) is engine-driven off `IMergeDiffService` + the conflict-index service methods; it never parses working-tree markers.
- **`Controls/`** — custom-drawn controls. `CommitGraphCanvas.cs` renders the commit graph (uses `Core/Graph`) and hosts right-click hit-testing; `GraphHitTester.cs` is the pure, unit-testable row/node/label hit-tester it delegates to (T-09; no Avalonia deps beyond `Point`/`Rect`). `IntraLineDiffTextBlock.cs` (T-13) is a `TextBlock` that splits a diff line into styled `Run`s from precomputed spans (`SpansSource` word emphasis + `TrailingWhitespaceSpan` marker) — contains no diff algorithm; emphasis/marker brushes resolve from theme tokens and re-resolve on `ThemeManager.ThemeChanged`. `ImageDiffControl.axaml(.cs)` (T-13) renders a detected image blob pair before/after with a size summary + opacity slider; **the swipe/onion-skin interaction feel is deferred** behind a `TODO(T-13 human-review)` marker.
- **`Converters/`** — `IValueConverter`s: `FileExtensionToIconConverter`, `BoolToOpacityConverter`.
- **`Services/`** — UI-facing service abstractions kept out of the ViewModels for testability. `IConfirmationService` / `DialogConfirmationService` gate destructive actions (e.g. the T-09 graph hard-reset) behind a confirmation dialog; a fake records the ask in tests.

### Tests & tooling

- **`GitLoom.Tests/`** — xUnit tests for Core (`GitServicesTests`, `GitServiceTagTests`, `GitServiceWorktreeTests`, `WorktreePorcelainParserTests`, `GitServiceDiffAgainstCommitTests`, `InteractiveRebaseServiceTests`, `PatchParserTests`, `PatchBuilderTests`, `GitServicePartialStagingTests`, `CommitGraphRouterTests`, `GraphHitTesterTests` (pure graph hit-testing, T-09), `CommitTimelineMenuTests` (graph context-menu construction, hard-reset/delete confirmation routing, drag merge/rebase flyout, T-09), `PinnedRefsTests` (pinned-ref persistence + router left-most ordering + migration apply, T-09), `GitServiceCurrentBranchFilterTests` (current-branch-only walk, T-09), `GitServiceRemoteTests` (T-10 remotes CRUD + push options: tracked-remote resolution, zero-remote typed throw, the force-with-lease succeed/stale-fail safety pair, set-upstream config, push-tags — the CLI push paths carry `RequiresGitCli`), `AutoFetchServiceTests` (T-10 auto-fetch: cadence/enable/skip-in-op/no-self-overlap/failure-count via a fake `IGitService` + the `RunCycleAsync` seam — no real waiting), `GitServiceBlameTests` (T-11 blame: per-line→SHA mapping over disjoint-edit commits, starting-at-prior-commit, typed throw on missing path, cache invalidation on HEAD change), `BlameCacheTests` (T-11 bounded-LRU eviction + per-repo invalidation), `BlameViewModelTests` (T-11 cancel-stale-load on rapid file switch), `GitServiceFileHistoryTests` (T-12 file history: touching-commits-newest-first, rename following with historical paths, blob-at-commit + typed binary/missing throw, adjacent-diff equals the tree diff, introduce/delete-then-gone/path-with-spaces edge cases, plus the pure line-range filter), `FileHistoryViewModelTests` (T-12 VM: newest-auto-select, selection→predecessor diff, introduction all-additions render, binary placeholder, line-filter narrowing via `FakeGitService`), `LineHistoryFilterTests` (T-12 pure hunk-intersection geometry — old/new-side, boundaries, omitted counts, multi-hunk), `IntraLineDiffTests` (T-13 pure intra-line word spans — single-word/full-rewrite/empty/whitespace-only/CRLF pinned ranges + surrogate-pair safety theory), `WhitespaceMarkersTests` (T-13 trailing-whitespace ranges), `ImageDiffDetectionTests` (T-13 image-candidate table + binary sniff + size summary), `GitServiceWhitespaceDiffTests` (T-13 `git diff -w` zero-hunks/real-hunks/staged/no-eol; `RequiresGitCli`), `DiffViewerViewModelDiffQualityTests` (T-13 VM: partial staging hidden in `-w` mode, syntax-toggle persistence, intra-line spans + trailing-whitespace + image-mode detection), `SettingsServiceTests`, `AppDbContextTests`, `GitHostDetectorTests`, `HostProviderRegistryTests` (T-14 provider resolution by host+kind + single-source `TokenUsername` + PAT-prompt acquire/throw-with-host), `SshKeyServiceTests` (T-14 ArgumentList argv construction + a REAL local ssh-keygen round trip — generate → files exist → `ListKeys` finds it → passphrase round-trips through the keyring, `RequiresGitCli`), `SecureKeyringTests` (T-14 round-trip + null-on-corrupt via the path override + encrypted-at-rest), `CredentialResolverTests` (T-14 SSH-vs-token credential selection), `AccountsViewModelTests` (T-14 known-host catalog + PAT store/remove + add-custom-host), `SignatureStatusParserTests` (T-15 pure: the `%G?` code table + batched-log parse incl. separator-in-signer/CRLF/empty), `GitServiceSigningTests` (T-15 integration, `RequiresGpg`: signed commit verifies + `%G?`==Good, signing-off→None, signed annotated tag verifies, signed-then-read-without-key→not-Good, bogus-key→typed throw without hang — an ephemeral throwaway GNUPGHOME + passphrase-less ed25519 key generated with the same gpg git invokes; skips cleanly when gpg is absent)) plus ViewModel/render tests. Shared test doubles live in `Fakes/` (`FakeGitService.cs` — a no-op `IGitService` fake for VM tests). `TestData/patches/` holds the real-git patch corpus (LF-locked) for the parser round-trip. The project references **both** `GitLoom.Core` and `GitLoom.App`. `Headless/TestAppBuilder.cs` (`[AvaloniaTestApplication]`) sets up headless Avalonia with Skia (`UseHeadlessDrawing=false`) so `[AvaloniaFact]` tests drive real Views and can capture rendered frames; `Headless/ResolverRenderHarness.cs` (conflict resolver), `Headless/TagUiRenderHarness.cs` (tag UI), `Headless/PartialStagingRenderHarness.cs` (partial-staging diff viewer), `Headless/InteractiveRebaseRenderHarness.cs` (interactive-rebase plan + fold rail), `Headless/GraphInteractionsRenderHarness.cs` (commit graph with a context menu open, driving right-click hit-testing, T-09), `Headless/RemotesUiRenderHarness.cs` (T-10 remotes-manager window, populated + empty states), `Headless/BlameRenderHarness.cs` (T-11 blame gutter — age-heat bar + author/sha/date against a fixture repo), `Headless/FileHistoryRenderHarness.cs` (T-12 file-history dialog — revision list + selected-vs-predecessor diff and the introducing-revision all-additions render against a fixture repo), `Headless/DiffQualityRenderHarness.cs` (T-13 intra-line emphasis + trailing-whitespace markers in the unified & side-by-side diff, and the ignore-whitespace mode hiding partial-staging actions), `Headless/AccountsSshRenderHarness.cs` (T-14 Accounts + SSH-keys preferences pages, asserting a non-empty rendered frame), and `Headless/SigningBadgeRenderHarness.cs` (T-15 commit-timeline with the verified/untrusted/bad signature badges — statuses assigned directly for a deterministic frame) render against real fixture repos, saving PNGs to `artifacts_headless/` (gitignored) for visual review.
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

GitLoom ships **one design system with switchable color themes**. The shape language, spacing, typography, and component classes are fixed; only the color palette changes per theme. **Midnight Loom** (layered charcoal + violet accent) is the default; **Daylight Loom** (light), **Command Deck**, **Atelier**, and **Loom Aurora** ship alongside it. The user switches themes via **File → Theme**; the choice persists in `UserPreferences.Theme`.

### Theming architecture (read this before touching any color)

- Each theme is one `ResourceDictionary` in **`GitLoom.App/Themes/<Key>.axaml`** defining the **full token contract** below. `App.axaml` merges `MidnightLoom.axaml` as the startup default.
- **`GitLoom.App/Theming/ThemeManager.cs`** swaps the merged dictionary at runtime, sets `RequestedThemeVariant` (so built-in Fluent chrome follows light/dark), persists the key, and raises `ThemeChanged`.
- **Color tokens are referenced with `{DynamicResource …}` — never `StaticResource`.** StaticResource is resolved once and will not update on a live theme switch. (`StaticResource` remains correct for theme-independent resources: icons and the `FontUi`/`FontMono` families.)
- **Code-drawn colors** resolve through `Application.Current.TryGetResource(key, app.ActualThemeVariant, …)` with a literal fallback, and long-lived visuals re-resolve on `ThemeManager.ThemeChanged`. `CommitGraphCanvas` is the reference pattern; `DiffViewerView`'s margin renderer and `AnalyticsViewModel.ThemeSkColor` follow it.
- **Adding a theme** = copy `MidnightLoom.axaml`, change values (define *every* token), register it in `ThemeManager.Themes`, add a File → Theme menu item. Nothing else.
- **Adding a token** = add it to **all** files in `Themes/` and to the table below. A token missing from one theme is a runtime bug the compiler cannot catch.

### The golden rule: no raw colors

**Never hardcode a hex color (`#RRGGBB`, `"White"`, `"Black"`) in a View or control.** Bind a named token with `DynamicResource`:

```xml
Foreground="{DynamicResource TextPrimary}"    <!-- yes -->
Foreground="{StaticResource TextPrimary}"     <!-- no — won't follow theme switches -->
Foreground="#CCCCCC"                          <!-- no -->
```

### Token contract (defined per theme in `Themes/*.axaml`)

Reference values are Midnight Loom's.

| Token | Purpose | Midnight |
|---|---|---|
| `SurfaceWindow` | window background the floating cards sit on | `#0F1115` |
| `SurfacePanel` | floating panel / sidebar card surface | `#14171C` |
| `SurfaceDeep` | deepest surface: code/diff editor | `#0B0D10` |
| `SurfaceCard` | inputs, raised cards, segment tracks | `#1A1E24` |
| `SurfaceHover` | hover / neutral selection | `#252B34` |
| `SurfaceHoverGhost` | `SurfaceHover` at 0 alpha — rest background for **ghost** buttons (transparent-looking, hover to `SurfaceHover`) so the fade never flashes white; see Depth & motion | `#00252B34` |
| `ButtonBg` | neutral button fill | `#1E232B` |
| `BorderHairline` | 1px borders, dividers | `#262B33` |
| `TextPrimary` / `TextMuted` | body & titles / metadata, hints | `#E6E9EF` / `#8A93A6` |
| `OnAccent` | text/icons on Accent, Success, Danger fills | `#0B0D10` |
| `AccentBrush` / `AccentHover` | signature accent, links, current branch / its hover | `#8B8BF5` / `#A5A5F8` |
| `AccentSelection` | translucent accent tint for selected rows/chips | `#268B8BF5` |
| `SuccessBrush` / `SuccessHover` | success, added | `#42B968` / `#5BCB7F` |
| `DangerBrush` / `DangerHover` | destructive, removed | `#F87171` / `#FA8C8C` |
| `WarningBrush` | warnings | `#E3B341` |
| `Lane1`–`Lane5` | commit-graph lanes (decoupled from semantics) | violet · rose · teal · amber · sky |
| `DiffAddedBg` / `DiffRemovedBg` | diff line backgrounds | `#11271B` / `#33191E` |
| `DiffAddedEmphasis` / `DiffRemovedEmphasis` | intra-line (word-level) emphasis over an added/removed line (T-13) | `#6642B968` / `#66F87171` |
| `DiffWhitespaceMarker` | trailing-whitespace tint (T-13) | `#55E3B341` |

Semantics rule: use tokens **by meaning, not by hue** — the same view must look right in all five themes. Never assume the accent is violet or the background is dark (Daylight Loom is light).

### Shape system — nothing is a bare rectangle

- **Corner radius scale:** `6` buttons/segments/list rows · `8` inputs, small cards, banners · `12` floating panel cards & overlay dialogs · `999` pills, chips, icon-button hovers, toasts. No other radii.
- **Floating panels:** workspace panes are rounded cards (`Border Classes="Card"` → `SurfacePanel`, hairline, radius 12) floating on `SurfaceWindow`, separated by **transparent 8px `GridSplitter` gutters** — never border-fused grid cells. Panels *inside* a card use `Transparent` backgrounds (the card provides the surface); the diff/editor card overrides its background to `SurfaceDeep`.
- **Pills & chips:** the navbar branch selector is a `Button.Pill`; ref/branch chips are radius-999 borders on `AccentSelection` with `AccentBrush` text; toasts are radius-999 pills with `OnAccent` text.
- **Selection:** selected rows get `AccentSelection` background plus a 3px rounded `AccentBrush` rail on the left edge (reserve the rail's column so layout doesn't shift — see the sidebar repo row).
- **Focus:** text inputs are radius 8 and get an `AccentBrush` border on `:focus` (global style — don't redefine per view).

### Component classes (defined once in `App.axaml` — pick by role, never inline the look)

| Class | Use for |
|---|---|
| `Button.Primary` | neutral/default actions (`ButtonBg` fill, hairline border) |
| `Button.Accent` | the **one** emphasized CTA per view (`AccentBrush` fill, `OnAccent` text) |
| `Button.Success` | positive/confirming actions (`SuccessBrush` fill, `OnAccent` text) |
| `Button.Danger` | destructive actions (`DangerBrush` fill, `OnAccent` text) |
| `Button.Secondary` | cancel/dismiss (transparent, muted, hairline) |
| `Button.IconButton` | toolbar/inline icon actions — circular hover, padding 6 |
| `Button.Pill` | capsule-shaped buttons (branch selector) |
| `Border.SegmentTrack` + `Button.Segment`(+`.Active`) | segmented switches (Commit/Shelf) — never underline tabs |
| `Border.Card` | floating panel card |
| `TextBlock.Mono` | SHAs, code, anything fixed-width (uses `FontMono`) |
| `CheckBox` / `CheckBox.FileRow` | auto-scaled 0.85 / 0.65 — don't inline `RenderTransform` |
| `PathIcon.Chevron`(+`.expanded`), `PathIcon.spinning` | shared chevron swap and spinner |

Rules: at most one `Accent` per view; anything destructive is `Danger` (no ad-hoc reds); cancels are `Secondary`; don't set `Background`/`Foreground` on a classed button (a muted `Foreground` on `Secondary` is the one tolerated exception, e.g. the stash Drop button).

### Typography

`FontUi` (Inter → Segoe UI fallback chain) is applied to every `Window` globally. `FontMono` is for SHAs, code, and diff text — use `TextBlock.Mono` or `FontFamily="{StaticResource FontMono}"`. Font sizes: `10–11` metadata/chips, `12–13` body/controls, `14` emphasis, `16–18` titles, `24` hero. Spacing scale: `4 / 5 / 8 / 10 / 15 / 20`.

### Icons

Shared `StreamGeometry` resources in `App.axaml`, rendered with `<PathIcon Data="{StaticResource SomeIcon}"/>` (icons are theme-independent — `StaticResource` is correct here). Sizes: **14×14** toolbar/inline, **10–12** chevrons/adornments, **18** nav, **48–64** empty-state art. Add new icons to `App.axaml`; never paste raw path data inline. Muted icon actions use `Foreground="{DynamicResource TextMuted}"`.

### Depth & motion

- Button hover backgrounds fade via a global 130ms `BrushTransition` — free on every button; don't add per-view hover animations.
- **Ghost buttons must rest on `SurfaceHoverGhost`, never `Background="Transparent"`.** The `BrushTransition` lerps color channels in straight (non-premultiplied) RGBA, and the `Transparent` keyword is `#00FFFFFF` (**white**, 0 alpha) — fading transparent→`SurfaceHover` ramps alpha while the RGB is still white, so the hover flashes white. `SurfaceHoverGhost` is `SurfaceHover` at 0 alpha, so only alpha changes across the fade (no color shift). Buttons with an *opaque* rest fill (`.Primary`/`.Accent`/`.Success`/`.Danger`) are unaffected. This is why `.IconButton`/`.Secondary`/`.Segment`/`.WindowButton` and inline ghost buttons set `Background="{DynamicResource SurfaceHoverGhost}"`.
- Overlays (command palette, confirmations): full-bleed scrim `#C0000000`, centered radius-12 card on `SurfacePanel` with hairline border and a soft `BoxShadow` (`0 10 30 0 #40000000`-family literals are fine).
- The commit graph draws with **round line caps** (`PenLineCap.Round`).
- Keep motion subtle: 120–150ms, opacity/brush only. No layout-affecting animations.

### Allowed literal-color exceptions

Semi-transparent black **scrims/shadows** (`#C0000000`, `#40000000`, `#80000000`, …); the repo icon **color-picker swatches** in `MainWindow`/`MainWindowViewModel` (those literals *are* the user-selectable colors, plus the default-dot `TargetNullValue`); **fallback literals** in code-behind theme-brush resolvers; and the legacy conflict-block tints in `ConflictResolverWindowViewModel` (that resolver is replaced wholesale by task T-04 — don't invest in it).

### Before you finish a UI change

Skim sibling views for the same element and match them. If you catch yourself typing a hex value, `StaticResource` on a color, a raw path geometry, an off-scale radius/padding, or a one-off tab/hover style — stop and use (or add) the token/class instead. New tokens go in **every** `Themes/*.axaml` file and the table above; new classes go in `App.axaml` and the class table. Verify with `dotnet build`, and sanity-check your change against both Midnight Loom and Daylight Loom mentally: if it assumes "dark", it's wrong.

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
