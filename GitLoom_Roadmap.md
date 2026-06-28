# GitLoom: Technical Roadmap & Architecture Blueprint

GitLoom is a premium, cross-platform desktop **Git GUI & Multi-Agent Control Center** built natively in C# and Avalonia UI. It serves as a command-and-control dashboard where developers manage complex Git workflows alongside a swarm of autonomous AI agents (Claude Code, AGY CLI, OpenCode) running concurrently in isolated Git worktrees.

---

## 1. Core Vision & Architectural Goals

- **The Swarm Coordinator:** A dual-mode orchestration system allowing the user to either manually pilot multiple agents, or delegate tasks to a "Lead Agent" that automatically spawns and manages worker sub-agents.
- **Persistent Unified Workspaces:** Agents and developers share the same DevContainer/Docker volume. Dependencies (`node_modules`), binaries, and network states (`localhost`) are instantly shared, eliminating "stale state" and cold-boot download penalties.
- **Zero-Conflict Concurrency:** Leverages `git worktree` to grant every agent an isolated physical directory while sharing the main repository's `.git` object database. Agents never lock each other's files.
- **Native OS Terminals:** Rejects slow, keystroke-swallowing browser WebViews (`xterm.js`). GitLoom uses native OS pseudo-terminals (`Pty.Net` via ConPTY/forkpty) rendered via Skia, replicating the flawless terminal robustness of JetBrains IDEs.

---

## 2. Technical Stack & Dependencies

- **Desktop Framework:** Avalonia UI (v11.1.3 - Stable)
- **MVVM Engine:** `CommunityToolkit.Mvvm` (v8.4.2)
- **Git Engine:** `LibGit2Sharp` (v0.30.0+ - standard native libgit2 bindings)
- **Local Database:** SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Windowing/Layout:** `Dock.Avalonia` (Modular layout pane snapping)
- **Terminal Backend:** `Pty.Net` (Cross-platform OS pseudo-terminal allocation)
- **Terminal Frontend:** `Iciclecreek.Avalonia.Terminal` (Native Skia VT100 ANSI parser)

---

## 3. Project Structure

```text
GitLoom/
├── GitLoom.Core/                       
│   ├── AppDbContext.cs                 
│   ├── Models/                         
│   ├── Services/                       
│   ├── Graph/                          
│   └── Agents/                         # Swarm Sandbox Engine
│       ├── IAgentExecutor.cs           
│       ├── WorktreeManager.cs          # Wraps `git worktree add/remove`
│       ├── PtyProcessShim.cs           # Manages Pty.Net streams and lifecycle
│       └── Orchestrator/
│           ├── CoordinatorAgent.cs     # Main agent chat interface
│           └── WorkerAgent.cs          # Isolated sub-task runner
│
├── GitLoom.App/                        
│   ├── ViewModels/                     
│   │   ├── ActivityBarViewModel.cs     # Manages sidebar agent stack & statuses
│   │   ├── AgentSandboxViewModel.cs    # Dock.Avalonia workspace layout state
│   │   └── TerminalViewModel.cs        # Pty.Net stream to Avalonia UI bridge
│   └── Views/                          
│       ├── ActivityBarView.axaml       # Split sidebar with scrollable bottom
│       └── AgentSandboxView.axaml      # Docking area for Diff, Staging, Terminal
│
└── GitLoom.Tests/                      
```

---

## 4. Phase-by-Phase Implementation Plan

### 🚀 Phase 1: Scaffolding & Workspace Manager (COMPLETED)
* **Phase 1.1: Project Scaffolding & Solution Setup (COMPLETED)**
  - [x] Initialize the `GitLoom.Core` class library, `GitLoom.App` Avalonia MVVM application, and `GitLoom.Tests` xUnit test suite on .NET 10.0.
  - [x] Wire assemblies together with project references and construct the solution map (`GitLoom.slnx`).
* **Phase 1.2: Dependencies & Local config.json Store (COMPLETED)**
  - [x] Install NuGet dependencies: `LibGit2Sharp`, `Microsoft.EntityFrameworkCore.Sqlite`, `LiveChartsCore.SkiaSharpView.Avalonia`.
  - [x] Design a strongly typed preferences model (`config.json`) targeting local AppData and implement O(1) in-memory settings service (theme).
* **Phase 1.3: Database Scaffolding & Bookmarks Store (COMPLETED)**
  - [x] Setup SQLite EF Core `AppDbContext` and migrations to handle Workspace Categories and Repository bookmarks.
* **Phase 1.4: Debounced Watcher & CLI Fallback scaffold (COMPLETED)**
  - [x] Implement the `GitService` interface supporting direct `LibGit2Sharp` methods.
  - [x] Design the strict `IDisposable` C-handle release block patterns.
  - [x] Implement a debounced `FileSystemWatcher` targeted at `.git/refs`, `.git/index`, and `.git/HEAD` that suppresses intermediate bursts and emits a debounced (300-500ms) final state reload.
* **Phase 1.5: Modern Shell & Sidebar UI (COMPLETED)**
  - [x] Build main window grid layout with a sidebar category browser, workspace tabs, and a local directory crawler to bookmark `.git` folders.

### 🛠️ Phase 2: Staging, Diffs, & Committing (COMPLETED)
* **Phase 2.1: Staging Status & Index Inspector (COMPLETED)**
  - [x] Query direct repo statuses via LibGit2Sharp to group files (Staged, Modified, Untracked, Deleted).
  - [x] Create a side panel in `RepoDashboardView` showing the file change trees with stage/unstage checkboxes.
* **Phase 2.2: Plain-Text DiffViewerControl (COMPLETED)**
  - [x] Implement line-by-line patch generation comparing working directories against the index or HEAD.
  - [x] Build the custom `DiffViewerControl` displaying unified or side-by-side lines with plain light-green/red line background accents (with tokenization deferred to keep UI thread load flat).
* **Phase 2.3: Commit Composer Pane (COMPLETED)**
  - [x] Design the commit message composer with emoji auto-replacements.
  - [x] Implement staged committing in `GitService`, handling author signatures, and triggering a post-commit local watcher refresh.
* **Phase 2.4: Push/Pull & Remote Sync (COMPLETED)**
  - [x] Query upstream tracking references to calculate `Ahead` and `Behind` commit indices.
  - [x] Implement LibGit2Sharp Network Push/Pull commands with credential callbacks.

### 🧬 Phase 3: High-Performance Commit History & Graph (COMPLETED)
* **Phase 3.1: Chunked Commit Querying & Virtual Timeline (COMPLETED)**
  - [x] Implement `GetRecentCommits` with skip/take chunked paging.
  - [x] Design scrollable commit card items inside Avalonia `ListBox` with `VirtualizingStackPanel`.
* **Phase 3.2: Isolated DAG Lane-Routing Engine (COMPLETED)**
  - [x] Create the `CommitGraphRouter` logical module inside `GitLoom.Core.Graph` completely decoupled from UI controls.
  - [x] Support incremental 500-commit topological mapping with a "Fringe State" contract to stitch seams between adjacent pages.
  - [x] Implement a comprehensive suite of unit tests under `GitLoom.Tests` validating octopus merges and complex overlapping track lanes.
* **Phase 3.3: Virtualized Vector CommitGraphCanvas (COMPLETED)**
  - [x] Build the custom `CommitGraphCanvas` control utilizing a DrawingContext.
  - [x] Bind canvas rendering to only draw glowing path tracks and node circles intersecting the visible viewport's row indexes.

### 🌿 Phase 4: Branch Management & Interactive Merging (IN PROGRESS)
* **Phase 4.1: Branch Tree & Checkout Control (COMPLETED)**
  - [x] Query local and remote heads to render a nested branch browser in the sidebar.
  - [x] Implement checkout safety validation checks (safely handling uncommitted changes).
* **Phase 4.2: Stashing & Creation Management (COMPLETED)**
  - [x] Build stashing list control and stash push/pop commands.
  - [x] Design new branch dialogs with safety tracking checkboxes.
* **Phase 4.3: Advanced Branch Context Menus (COMPLETED)**
  - [x] Implement a deeply nested UI architecture for branch interactions.
  - [x] Implement dynamic `MenuItemViewModel` trees and `TreeDataTemplate` rendering.
  - [x] Hook up Checkout, New Branch, Update, Push, and Delete Branch safely.
* **Phase 4.4: In-App Code Editor & Conflict Resolution (3-Way Merge)**
  - Upgrade the DiffViewer to an interactive AvaloniaEdit control for direct code modifications and quick fixes.
  - Implement a 3-way merge UI and parsing engine for resolving merge conflicts directly within the app.
* **Phase 4.5: Advanced Git Operations (Rebase, Worktrees, Diffs)**
  - Implement backend `LibGit2Sharp` logic for rebasing and advanced interactive rebasing.
  - Implement Git Worktree integration natively.
  - Implement working tree diffs against specific arbitrary commits.

### 📊 Phase 5: Repository Analytics & Churn (Premium Polish)
* **Phase 5.1: Asynchronous gitignore-Aware Language Parser**
  - Build directory tree crawler that parses `.gitignore` recursively.
  - Process language byte counts in the background and wire data up to SkiaSharp Donut Charts.
* **Phase 5.2: Churn & Punch Card Calculations**
  - Asynchronously traverse history to compile Code Churn stats (net additions/deletions over time) and developer activity Punch Cards.
* **Phase 5.3: UI Transitions & Micro-Animations**
  - Apply clean transitions to tab navigation and analytics loading indicators.
* **Phase 5.4: Ghost Loading / Skeleton Screens**
  - Implement a highly polished ghost loading/skeleton screen overlay for the `RepoDashboardView`.
  - When opening a new repository, render a pulsing skeleton frame of the Staging Panel, Diff Viewer, and Timeline while LibGit2Sharp executes parsing in a background thread.
  - Ensures the UI immediately reacts to repository switching without hard blocking or appearing unstyled during I/O delays.

### ☁️ Phase 6: Agent Profiles & Secure Keyring Sync (Opt-In Extension)
* **Phase 6.1: Audited Cross-Platform Secure Keyring**
  - Implement a JetBrains-style internal credential manager that intercepts Git auth prompts and caches credentials securely using OS native storage (DPAPI on Windows, Keychain on macOS, Secret Service on Linux).
* **Phase 6.2: Decentralized Device Flow Client**
  - Implement secure client-to-GitHub OAuth 2.0 Device Flow browser integrations.
* **Phase 6.3: Remote Repository Cloner panel**
  - Fetch user repository lists asynchronously over REST.
  - Design a dedicated "Clone Remote Repository" dashboard allowing one-click staging into local categories.
* **Phase 6.4: LLM API Key Management (BYOK)**
  - Expand the secure keyring (DPAPI/Keychain) to safely encrypt and store user-provided OpenAI/Anthropic API keys locally, keeping them out of plaintext configuration files.

### 🤖 Phase 7: Integrated Multi-Agent Control Center
* **Phase 7.1: The JetBrains Terminal Engine (`Pty.Net`)**
  - Implement native OS pseudo-terminals (ConPTY for Windows, forkpty for Linux/macOS) to prevent interactive CLIs from degrading.
  - Bind `Pty.Net` streams to `Iciclecreek.Avalonia.Terminal` for Skia-accelerated ANSI rendering.
* **Phase 7.2: Unified Persistent Workspaces & Swarm Mechanics**
  - Automate `git branch` and `git worktree add` for agent isolation, allowing concurrent execution without file-locking collisions.
  - Run agents in the same Docker volume/DevContainer to ensure zero cold-boot dependency download penalties and local network parity.
* **Phase 7.3: The Agent Lifecycle & Merging Workflow**
  - Implement the "Keep-Alive Mid-Stream Merge" (Commit dirty worktree, merge to main, fast-forward worktree).
  - Implement Sibling Worktree Merging to allow agents to share code (e.g. `git merge agent/db-schema`) before integrating to `main`.
  - Automate teardown and cleanup routines (kill PTY, force remove worktree, delete temporary agent branch).
* **Phase 7.4: The Split Activity Bar & Docking UI**
  - Implement `Dock.Avalonia` for customizable Agent Workspaces (containing the Native Terminal, Code Diff Viewer, and Staging Tree).
  - Build the split Activity Bar (Coordinator pinned top, dynamic LIFO worker list bottom) with colored Status Micro-Badges (Running, Awaiting Merge, Conflict).
  - Animate the Coordinator Tab to pulse red/yellow when human intervention or manual approvals are required.
* **Phase 7.5: Dual-Mode Orchestration**
  - Manual Mode: Enable `[+]` spawning and Read-Write terminal interactions for direct user orchestration.
  - Coordinator Mode: Implement Read-Only terminal locking for workers, enforce configurable Max Subagent Limits, and provide a Human Approval Gate for merges.
  - Build the "Master PR Dashboard" overview for the Coordinator Tab to surface readiness states and trigger 3-way visual merge resolutions.
