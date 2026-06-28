# GitLoom: Technical Roadmap & Architecture Blueprint

GitLoom is a premium, cross-platform desktop **Git GUI & Multi-Agent Control Center** built natively in C# and Avalonia UI. It serves as a command-and-control dashboard where developers manage complex Git workflows alongside a swarm of autonomous AI agents (Claude Code, AGY CLI, OpenCode) running concurrently in isolated Git worktrees.

---

## 1. Core Vision & Architectural Goals

- **The Swarm Coordinator:** A dual-mode orchestration system allowing the user to either manually pilot multiple agents, or delegate tasks to a "Lead Agent" that automatically spawns and manages worker sub-agents.
- **Client-Server Split Architecture:** Bypasses Windows-to-WSL 9P file share latency. The Avalonia UI runs as a native Windows client, communicating with a headless `.NET` daemon running in a dedicated `GitLoomOS` Linux boundary.
- **The Nested Sandbox (Persistent Per-Repo Docker Isolation):** GitLoom drops the bloated Docker Desktop for Windows. Instead, it provisions a silent, headless WSL2 instance containing the raw Linux Docker Engine. Each repository is launched in its own persistent Docker container. These containers act as permanent sandboxes for each project, ensuring installs are cached, agent sessions are instantly resumable, and agents cannot corrupt the host or other projects.
- **Zero-Conflict Concurrency:** Within a repository's secure container, GitLoom leverages `git worktree` to grant concurrent agents isolated physical directories while sharing the `.git` database, preventing file-locking collisions.
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
  - Frame `Pty.Net` bytes into 16ms chunks on the headless Linux Server before streaming over WebSockets/gRPC to prevent HTTP/2 multiplexer flooding on the Avalonia Client.
  - Implement zero-allocation buffers, 60 FPS `DispatcherTimer` UI rendering, and strict 10,000-line scrollback limits.
* **Phase 7.2: Client-Server Swarm Mechanics & Nested Sandboxes**
  - Implement the `GitLoomOS` Bootstrapper: Silently import a minimal Alpine/Ubuntu WSL2 tarball and launch the raw `dockerd` (Docker Engine) inside it, completely bypassing Docker Desktop for Windows.
  - Implement Persistent Per-Repo Jails: Automate container lifecycles (`docker create`, `start`, `stop`) so that each repository has a permanent container, allowing instant resumption of agent sessions without re-installing dependencies.
  - Automate `git branch` and `git worktree add` within the container to isolate concurrent agents working on the same repository.
  - Implement strict "Unit of Work" threading for `LibGit2Sharp` (wrapping every operation in `using`) to prevent `AccessViolationException`s.
  - Implement Zombie Swarm Prevention: On boot, mathematically verify agent death by reading `PID` and `Process.StartTime` (Ticks) from `.gitloom.lock` JSON payloads, completely bypassing OS PID recycling false-positives.
* **Phase 7.3: The Agent Lifecycle & Merging Workflow**
  - Implement Worktree-Safe Syncing: Suspend agent, commit worktree, and `git rebase main` without checking out `main`.
  - Implement the "Middle Manager" Architecture: The Coordinator acts strictly as a manager (no code, no worktree) that spawns and monitors isolated Worker Agents. 
  - Implement Foreground Integration: Abandon automated background merges. Workers flag themselves for human review. Humans manually trigger "Merge to Main" in the foreground, making Primary Repository merges completely safe and expected.
  - Implement the Cooperative Yield Protocol (stateless IPC triads like `[IPC_UPDATE_REQUESTED]`/`READY`) to safely pause agents before rebasing, preventing race conditions and `.git/index.lock` collisions.
  - Automate teardown and cleanup routines (kill PTY, force remove worktree, delete temporary agent branch, and explicitly `Close()` floating `Dock.Avalonia` windows to prevent UI leaks).
* **Phase 7.4: The Split Activity Bar & Docking UI**
  - Implement `Dock.Avalonia` for customizable Agent Workspaces (containing the Native Terminal, Code Diff Viewer, and Staging Tree).
  - Build the split Activity Bar (Coordinator pinned top, dynamic LIFO worker list bottom) with colored Status Micro-Badges (Running, Awaiting Merge, Conflict).
  - Animate the Coordinator Tab to pulse red/yellow when human intervention or manual approvals are required.
* **Phase 7.5: Dual-Mode Orchestration**
  - Manual Mode: Enable `[+]` spawning and Read-Write terminal interactions for direct user orchestration.
  - Coordinator Mode: Implement Read-Only terminal locking for workers and enforce configurable Max Subagent Limits.
  - The Human Handoff: Instead of executing merges, the Coordinator surfaces readiness states. Humans manually trigger "Merge to Main" and resolve conflicts strictly in the foreground UI, completely decoupling the Coordinator from Git conflict states.

---

# GitLoom Velopack Distribution & OOBE First-Run Roadmap

GitLoom utilizes **Velopack** for zero-friction cross-platform distribution (Windows `.exe`, macOS `.dmg`, Linux `.AppImage`). Instead of a separate clunky installer, Velopack silently drops the binaries onto the system and immediately launches GitLoom. 

The "Installer" is actually an **Out of Box Experience (OOBE) First-Run Wizard** built directly into the Avalonia UI. This allows GitLoom to use the exact same beautiful setup screens across all operating systems while dynamically branching the system-level heavy lifting (WSL for Windows, Colima for Mac, native Docker for Linux).

---

## 1. Core Vision & Goals

- **Upfront Elevation:** By requesting Administrator privileges natively via the Installer, we bypass jarring UAC prompts inside the app.
- **The Setup Fork:** The installer itself acts as the filter, tailoring its UI, copy, and transparency based on whether the user selects "Vibe Coder" or "Developer".
- **Deep OS Integration:** The installer registers context menus ("Open with GitLoom") and custom URL handlers (`gitloom://`) to enable flawless headless OAuth flows later on.
- **Pristine Teardown:** A proper Windows uninstaller guarantees that if a user leaves, the hidden Linux VM and all isolated Docker containers are completely wiped from their system, leaving no orphaned virtual drives.

---

## 2. Phase-by-Phase Installer Roadmap

### 🚀 Phase 1: The OOBE Fork & Diagnostics
* **Phase 1.1: The Identity Question**
  - Upon the very first launch of `GitLoom.exe`, the Avalonia app presents the OOBE screen: "How do you want to build?" (Vibe Coder vs Developer). This locks in the tone for the rest of the setup.
* **Phase 1.2: System Hardware Diagnostics**
  - The app silently queries the host machine. (On Windows: checks WSL2. On Mac: checks Colima. On Linux: checks Docker Engine).

### 🛠️ Phase 2: OS Enablement & The Unavoidable Reboot
* **Phase 2.1: Feature Enablement (Conditional)**
  - If WSL2 is missing, the installer triggers Windows APIs to enable the Virtual Machine Platform. 
  - *Dev Mode UX:* Shows the raw PowerShell commands being executed.
  - *Vibe Mode UX:* Shows a friendly "Constructing your secure sandbox" message.
* **Phase 2.2: The Auto-Resume Reboot**
  - If feature enablement occurs, the installer prompts for a reboot and sets a `RunOnce` registry key so the installer resumes exactly where it left off after the computer restarts.

### 📦 Phase 3: The `GitLoomOS` Extraction
* **Phase 3.1: The Pre-Baked Payload (The Tarball)**
  - The installer extracts `GitLoomOS.tar.gz`. This tarball is a stripped-down Alpine/Debian root filesystem containing only `dockerd` (Docker Engine), `git`, Node.js, and Python. It does *not* contain the agents themselves, keeping it ultra-lightweight.
* **Phase 3.2: Silent Import**
  - The installer executes `wsl --import GitLoomEnv` in the background to instantiate the Linux VM.

### 🔗 Phase 4: Windows Deep Integration
* **Phase 4.1: Context Menus**
  - Register registry keys to add "Open with GitLoom" to Windows File Explorer context menus.
* **Phase 4.2: OAuth URL Protocol**
  - Register the `gitloom://` protocol handler in Windows to seamlessly route CLI browser login callbacks natively.

### ✨ Phase 5: Agent Provisioning & App Launch
* **Phase 5.1: Agent Selection & Authentication**
  - Only after the OS and WSL environment are fully verified and running does the wizard prompt the user to select their Agentic CLIs (Claude Code, AGY, OpenCode) and authenticate via API Key or browser OAuth.
* **Phase 5.2: Dynamic Setup**
  - The installer dynamically runs `npm install -g` within the new WSL instance to fetch the absolute latest versions of the chosen agents and injects the authentication tokens.
* **Phase 5.3: The Clean Launch**
  - The installer finishes and launches `GitLoom.exe`, dropping the user instantly into a fully authenticated, ready-to-code workspace.

---

# GitLoom Vibe Mode: Technical Roadmap & Vision

GitLoom "Vibe Mode" is a specialized workspace within GitLoom designed specifically for "vibe coders"—users with little to no traditional development experience. 

It achieves true zero-knowledge abstraction not by faking things in the UI, but by introducing a **"Virtual User" (The Vibe Orchestrator)** directly into the GitLoom backend. Underneath, Vibe Mode relies on the exact same powerful Linux/WSL2 environment, `Pty.Net` terminal streams, and external Agentic CLIs (Claude Code, AGY) as Developer Mode. However, instead of a human managing the terminals and commits, the Orchestrator drives them completely automatically behind the scenes.

---

## 1. Core Vision & Goals

- **Unified, Zero-Click Foundation:** Setup is identical across the entire GitLoom app (Dev Mode and Vibe Mode). GitLoom manages a pre-baked, isolated Linux environment (`GitLoomOS`) silently.
- **Native CLI Authentication:** API keys are entirely optional. Users can log in using the native OAuth flows provided by the agentic CLIs (like Claude Code's browser login). GitLoom seamlessly bridges these CLI prompts to the Windows UI.
- **Unaltered Core Technologies:** The agentic CLIs, WSL2 boundaries, and Git Worktrees function exactly as they do normally. The only difference is they are driven programmatically by the backend orchestrator rather than manually by a human developer.
- **The "Dumb" Frontend:** The Avalonia UI is stripped of all Git and Terminal logic. It becomes incredibly lightweight—purely a Chat Interface and a Live App Preview that subscribes to simplified events from the backend.
- **Offline Durability:** Because the backend is self-driving, a vibe coder can close the GitLoom UI entirely. The background daemon will continue compiling code, hitting errors, auto-healing them via the CLI agents, and saving Git checkpoints.

---

## 2. Phase-by-Phase Vibe Mode Roadmap

### 🌟 Phase 1: The Unified App Foundation & Orchestrator
* **Phase 1.1: The `GitLoomOS` Pre-Baked Sandbox**
  - Implement a universal setup flow for the entire application. GitLoom silently executes `wsl --import` using a bundled, lightweight Linux tarball. This guarantees a pristine environment with Node, Python, and Git pre-installed, permanently bypassing Windows native NPM issues for both Dev and Vibe modes.
* **Phase 1.2: The `VibeOrchestrator` Engine**
  - Build the backend service that acts as the "puppet master." This service will natively hook into the local `Pty.Net` streams to manage the agent CLIs programmatically.
* **Phase 1.3: Headless OAuth Authentication**
  - Ensure users don't need API keys. When an agent CLI requires an OAuth login, the backend detects the login URL in the stream and sends an event to the Windows UI to securely open the user's browser, completing the headless CLI login.

### 🛡️ Phase 2: Autonomous Git Abstraction
* **Phase 2.1: Auto-Checkpoints**
  - Implement logic within the `VibeOrchestrator` to automatically generate clean `LibGit2Sharp` commits (Checkpoints) every time the agent successfully completes a chat request.
* **Phase 2.2: Autonomous Conflict Resolution**
  - Teach the `VibeOrchestrator` to catch Git merge conflicts in the backend. Instead of pausing for human intervention, it automatically feeds the conflict markers into the Agent CLI to resolve.

### 🚑 Phase 3: In-Memory Auto-Healing
* **Phase 3.1: Stream Interception**
  - The `VibeOrchestrator` monitors the Dev Server `Pty.Net` output streams in memory on the Linux server. 
* **Phase 3.2: The Autonomous Fix Loop**
  - When a crash occurs, the Orchestrator instantly captures the stack trace and pipes it directly into the active Agent CLI's input stream with a prompt to fix it, bypassing the frontend entirely.

### 🎨 Phase 4: The Vibe Mode UI Interface
* **Phase 4.1: Seamless Mode Toggling**
  - Build the Avalonia UI switch that hides all Developer Mode dock panels and transitions to the simple Chat/Preview layout.
* **Phase 4.2: Live Embedded Preview (WebView2/CefGlue)**
  - Integrate a native embedded browser panel that auto-navigates to the localhost port managed by the backend dev server.
* **Phase 4.3: Chat-to-Orchestrator Bridge**
  - The UI captures natural language from the vibe coder and sends it to the backend `VibeOrchestrator`, which then proxies it to the underlying Agent CLI.

### 🚀 Phase 5: One-Click Deployment
* **Phase 5.1: Cloud Integrations**
  - Integrate secure OAuth flows for deployment platforms like Vercel or Netlify.
* **Phase 5.2: "Publish to Web"**
  - Create a single UI button that triggers the `VibeOrchestrator` to coordinate a final commit, push to GitHub, and trigger a cloud build, returning a live URL.
