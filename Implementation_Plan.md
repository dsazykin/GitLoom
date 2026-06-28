# GitLoom Multi-Agent Control Center: Implementation Details

This document outlines the concrete implementation specifications for integrating concurrent AI CLI agents (Claude Code, AGY, OpenCode) into the GitLoom architecture using native OS terminals and Git worktrees.

---

## 1. The Environment: The Client-Server Split Architecture

GitLoom abandons Docker Desktop for Windows and avoids the notorious 9P file share latency. Instead, it relies on a **Containerized Git Sandbox Architecture** for zero-friction setup, maximum security, and blazing execution speed.
*   **The Nested Engine (WSL2 -> Raw Docker):** The GitLoom Windows installer silently provisions a private, lightweight WSL2 instance (`GitLoomOS`). Inside this native Linux boundary, GitLoom runs the raw, open-source Docker Engine (`dockerd`) in the background.
*   **Persistent Per-Repo Isolation (Blast Radius Protection):** When a user opens a repository, `GitLoom.Server` creates a dedicated, persistent Docker container. The Agentic CLIs are completely jailed within this container, preserving `node_modules` caches and agent sessions across app restarts.
*   **The "No-Mount" Clone (Speed & Friction Solution):** To avoid 9P file latency, GitLoom does *not* mount the user's Windows directory (`C:\Code\Project`) into the container. Instead, it creates a secondary `git clone` of the repository directly into a native Linux Docker Volume attached to the container. The user works natively on Windows, and the agent works natively on Linux.
*   **The Bridge Protocol (Git Fetch):** The headless `.NET` daemon inside WSL monitors the agent's commits. When an agent creates a checkpoint in its Docker volume, the Windows Avalonia UI automatically runs `git fetch` on the host repository, bringing the agent's branch over for human review and foreground merging.

---

## 2. Terminal Emulation: The JetBrains Native Approach

To ensure interactive CLIs behave perfectly (accepting keystrokes, rendering curses UIs), GitLoom rejects web-based `xterm.js` WebViews in favor of a fully native stack.

### A. The Backend: `Pty.Net`
GitLoom uses `Pty.Net` to allocate a true OS-level Pseudo-Terminal.
*   **Windows:** Leverages the `ConPTY` API.
*   **Linux/macOS:** Leverages standard `forkpty`.
*   **Implementation:** `PtyProvider.SpawnAsync(new PtyOptions { App = "claude", Cwd = worktreePath })` forces the OS to tell the CLI it is attached to a physical terminal window, preventing `isatty()` failures.

### B. The Frontend: Native Avalonia VT100
*   GitLoom bounds the PTY streams to a native Avalonia control (e.g., `Iciclecreek.Avalonia.Terminal`). 
*   This parses VT100/ANSI color escapes natively and renders them via Skia hardware acceleration. Keystrokes (like `Ctrl+C`) are intercepted by the Avalonia Window and sent directly into the PTY byte stream, eliminating browser-bridge input lag.
*   **Performance Throttling:** Raw streams are read via zero-allocation buffers (`ArrayPool<byte>`) into a background Ring Buffer. A `DispatcherTimer` set to ~16ms (60 FPS) invalidates the control, preventing UI Thread locks during heavy logs.
*   **Memory Bounds:** A strict 10,000-line circular scrollback limit prevents `OutOfMemoryException`s.

---

## 3. Swarm Mechanics: Git Worktree Isolation

Multiple agents can run concurrently on the same repository without file-locking collisions.

*   **Initialization (The Spawner):** When an agent is created, GitLoom executes:
    1. `git branch agent/uuid-1234 main`
    2. `git worktree add ../project_agent_uuid-1234 agent/uuid-1234`
*   **Execution:** The `Pty.Net` process is spawned with its Current Working Directory (CWD) locked to `../project_agent_uuid-1234`. The agent works in complete physical isolation but shares the `.git` database.

---

## 4. UI/UX Architecture: Mission Control

The UI is built to manage the swarm like an Air Traffic Controller, relying on two core components:

### A. The Split Activity Bar (Sidebar)
Implemented as a 2-Row `Grid`:
*   **Top Half (Pinned):** Contains core app icons (Files, Staging, Git Graph) and the **Main Coordinator Agent** tab. This tab visually pulses red or yellow when human intervention (conflict resolution or manual approval) is required.
*   **Bottom Half (Dynamic):** An invisible 50/50 split containing a `VirtualizingStackPanel` within an `ItemsControl` bound to an `ObservableCollection<AgentViewModel>`. UI Virtualization prevents the Layout Avalanche when spawning 50+ agents.
*   **LIFO Stacking:** New agents are spawned by calling `Collection.Insert(0, newAgent)`. This ensures the newest agent appears at the absolute top of the scrollable list.
*   **Memory Management:** The MVVM architecture enforces the Weak Event Pattern (`WeakReferenceMessenger`) and deterministic teardown (`Dispose()` on tab close) to prevent dangling Skia visual trees.
*   **Status Micro-Badges:** Each agent tab has a colored dot bound to its state (🟢 Running, 🟡 Awaiting Merge, 🔴 Conflict).

### B. The Dockable Sandbox (`Dock.Avalonia`)
When an Agent Tab is clicked, the center workspace loads a customizable docking layout specifically for that agent.
*   The user can drag, snap, or float panels.
*   Panels include: The Native Terminal, the Code Diff Viewer (comparing `agent-branch` to `main`), and the Staging Tree.

### C. The Master PR Dashboard & Coordinator Overview
*   **Central Control Tab:** The Main Coordinator Tab features a high-level overview dashboard displaying the active progress and states of all current sub-agents, replacing the need to click into individual terminals to check status.
*   **Convergence View:** When the Coordinator attempts to merge and hits a conflict or requires approval, the center workspace transitions to a dedicated "Master PR Dashboard." This provides a centralized place to review ready-to-merge agents, view conflicts, and click "Resolve" using the 3-way merge tool.

---

## 5. Dual Operating Modes

### A. Coordinator Mode (Delegated Swarm)
*   **Workflow:** The user chats with the Main Agent tab. The Main Agent automatically spawns Worker Agents via a background API (respecting a user-defined **Max Subagents Limit** to prevent resource exhaustion).
*   **Read-Only Terminals:** When the user clicks a Worker Agent on the sidebar, the Avalonia Terminal control is set to `IsReadOnly = true`. The user can watch the CLI output stream like a CCTV monitor, but cannot type into it. A banner warns: 🔒 *Managed by Coordinator*.
*   **Convergence:** The Coordinator attempts to merge finished worktrees. If configured with a **Human Approval Gate**, it pauses to await manual approval before any merge. If conflicts arise, GitLoom surfaces its native 3-way visual merge tool for the user to resolve manually.

### B. Manual Mode (User Orchestrator)
*   **Workflow:** A `[+]` button appears on the Activity Bar. The user clicks it to manually spawn independent agents.
*   **Read-Write Terminals:** The Avalonia Terminal control is active. The user types prompts directly into the PTY.

---

## 6. The Agent Lifecycle & Merging Workflow

GitLoom safely manages the underlying Git state throughout the agent's life:

1.  **The Middle Manager Architecture (Anti-Poisoning):** GitLoom strictly prevents "Working Directory Poisoning" by completely abandoning automated background merges and disposable integration worktrees.
    *   **Strict Isolation:** Every Worker Agent operates in its own completely isolated `git worktree`. They never merge into each other. This guarantees strict feature isolation for easy testing.
    *   **The Middle Manager (Coordinator):** The Coordinator agent does not write code, does not merge branches, and does not have a worktree. It acts purely as an Engineering Manager, using internal APIs to spawn, prompt, and monitor Worker Agents. 
    *   **User-Initiated Integration:** When a worker succeeds, the Coordinator flags the UI as `Awaiting Human Review`. The human securely checks out the worker's branch to test it. Once satisfied, the human clicks "Merge to Main" in the UI. Because this is a deliberate foreground action, executing it on the Primary Repository is perfectly safe and expected.
    *   **Process Suspension:** Before any Git mutation (like Keep-Alive syncing), GitLoom enforces the Cooperative Yield Protocol (`[IPC_UPDATE_REQUESTED]`) to ensure the agent has safely paused execution.
2.  **Cross-Agent Dependency Resolution:** 
    *   If Worker B requires code being written by Worker A, the Coordinator does not attempt to cross-merge their worktrees. Instead, the Coordinator instructs Worker B to wait. Once Worker A finishes and the human merges Worker A into `main`, the Coordinator instructs Worker B to perform a standard Keep-Alive Rebase against `main` to cleanly inherit the dependencies.
3.  **Teardown & Cleanup:**
    *   When closed, GitLoom prompts for unmerged changes (Merge or Discard).
    *   GitLoom kills the PTY process.
    *   GitLoom executes `git worktree remove ../project_agent_uuid-1234 --force`.
    *   GitLoom deletes the temporary branch, leaving the host pristine.

## 7. AI Autonomous Implementation Instructions (Phases 6.4 - 7.5)

The following directives are formatted as strict, autonomous implementation plans for an AI agent tasked with building out the remaining architecture phases.

### 🤖 Instruction: Phase 6.4: LLM API Key Management (BYOK)
**Objective:** Securely store user-provided LLM API keys (OpenAI, Anthropic) without ever writing them to a plaintext file.
**Implementation Steps:**
1.  **OS-Native Keyring Integration:** In `GitLoom.Core.Security`, implement a cross-platform `ISecureKeyStore` interface.
2.  **Windows Implementation:** Use `ProtectedData.Protect` (DPAPI) to encrypt the API key before writing it to `config.json`. Alternatively, use the Windows Credential Manager API.
3.  **macOS/Linux Fallbacks:** If compiling for cross-platform, utilize `Security.framework` (Keychain) for macOS and `libsecret` (Secret Service API) for Linux.
4.  **ViewModel Binding:** Create an `ApiKeySettingsViewModel` with a `PasswordBox` in Avalonia to allow the user to input keys. Ensure the raw string is never held in memory longer than necessary (use `SecureString` if bridging to unmanaged APIs).
5.  **Environment Injection:** When spawning agents via `IAgentExecutor`, decrypt the key in-memory and inject it securely into the agent's environment (e.g., via a temporary `.env` file that is immediately deleted, or passed securely over an IPC pipe).

### 🤖 Instruction: Phase 7.1: The JetBrains Terminal Engine (`Pty.Net`)
**Objective:** Replace standard process redirection with a native Pseudo-Terminal to ensure CLI tools function without `isatty()` crashes.
**Implementation Steps:**
1.  **Backend Dependency:** Install the `Pty.Net` NuGet package in `GitLoom.Core`.
2.  **Process Spawner:** Build `PtyProcessShim.cs` that invokes `PtyProvider.SpawnAsync()`. Pass `PtyOptions` configuring the `App` (e.g., `npx claude`), the arguments, and crucially, set `Cwd` to the isolated worktree directory.
3.  **Frontend Binding:** In `GitLoom.App`, integrate the `Iciclecreek.Avalonia.Terminal` control (or `XTerm.NET`).
4.  **Stream Piping & Throttling:** The `GitLoom.Server` collects `Pty.Net` bytes into an `ArrayBufferWriter<byte>` and flushes them across the gRPC stream exactly once every 16ms (60 FPS). This prevents HTTP/2 multiplexer flooding and subsequent Avalonia UI freezes from deserialization overhead.
5.  **Memory Guard:** Implement a strict circular-overwrite scrollback limit (e.g., 10,000 lines) on the terminal's backing model.

### 🤖 Instruction: Phase 7.2: The Containerized Sandbox & Git Fetch Bridge
**Objective:** Securely isolate agents per-repository using Docker, and then isolate concurrent agent threads using Git Worktrees, completely abandoning Docker Desktop and 9P volume mounts.
**Implementation Steps:**
1.  **The `GitLoomOS` Bootstrapper:** On first launch, the Windows App executes `wsl --import GitLoomEnv` using a bundled minimal Linux tarball. It then starts `dockerd` inside this WSL instance via a background daemon.
2.  **Persistent Container Lifecycle & Auth Mounting:** When a project is loaded, check if its container exists. If not, GitLoom executes `docker create`. Crucially, it mounts the global `GitLoomOS` authentication directories as read-only volumes (e.g., `-v ~/.claude:/root/.claude:ro`). Then `docker start` is executed.
3.  **The "No-Mount" Clone Manager:** Execute `git clone` from the Windows host path directly into a native Linux Docker Volume attached to the container. Do NOT bind mount the Windows path.
4.  **Just-In-Time (JIT) Environment Provisioning:** If the repository lacks a `.devcontainer` definition, automatically scan the codebase (e.g., detecting `package.json`, `pom.xml`) and install the required toolchains into the persistent container.
5.  **Worktree Manager:** Create `WorktreeManager.cs`. Within the repository's containerized clone, execute `git worktree add ../agent_workspace/agent_{id} agent/{id}` to physically separate concurrent agent edits.
6.  **Zombie Swarm Prevention:** On launch, read the `.gitloom.lock` JSON payload containing the agent's Docker `ContainerId` and `PID`. If the container or process is dead, execute `git worktree prune` and clear the branch.
7.  **PTY Routing & Execution:** The `GitLoom.Server` executes `docker exec -it <container_id> <agent_cli>` to spawn the agent. The `Pty.Net` streams capture the container output and pipe it over gRPC to the Windows Avalonia UI.
8.  **The Git Fetch Bridge:** Establish a polling or file-watcher mechanism. When the agent pushes or commits to its local branch in the Docker volume, GitLoom executes `git fetch` on the Windows host repository to seamlessly sync the changes for human review.

### 🤖 Instruction: Phase 7.3: The Agent Lifecycle & Merging Workflow
**Objective:** Manage the lifecycle of agent worktrees, including cross-agent code sharing and pristine teardowns.
**Implementation Steps:**
1.  **State Failsafes & Process Suspension:** Before executing Git commands, check for `.git/worktrees/<id>/rebase-merge/` or detached HEAD; abort if found. Implement the **Cooperative Yield Protocol**. The daemon sends `[IPC_UPDATE_REQUESTED]` and waits asynchronously for `[IPC_UPDATE_READY]`. This stateless triad avoids 5-second timeout race conditions that would brick blocked agents. Wrap successful Git operations in exponential backoff retry loops for `index.lock` errors.
2.  **The Middle Manager Lifecycle (Foreground Integration):** Prevent Working Directory Poisoning (injecting conflicts into the user's active IDE) by relying on foreground merges.
    *   **Keep-Alive Rebase (Sync only):** Suspend the agent. Inside the agent's worktree, stage, commit, and execute `git rebase main` (referencing without checkout). Resume.
    *   **Awaiting Human Review:** The Coordinator agent calls an internal API to flag the worker UI. The worker pauses indefinitely.
    *   **Foreground Merge (User Action):** The human tests the worker's branch. When the human clicks "Merge to Main" in the GitLoom UI, GitLoom executes `git merge agent/{id}` directly on the Primary Repository, resolving conflicts in the foreground via the IDE.
3.  **Strict Isolation Enforcement:** Do NOT implement automated cross-agent sibling merges. All worktrees remain strictly isolated until merged into `main` by the human.
4.  **Cleanup Engine:** Implement `IDisposable` on the agent context. On teardown, forcefully kill the `Pty.Net` process. Execute `git worktree remove --force {path}`. Execute `git branch -D agent/{id}`. Verify the file system is entirely clean.

### 🤖 Instruction: Phase 7.4: The Split Activity Bar & Docking UI
**Objective:** Build the ATC (Air Traffic Control) interface for the swarm.
**Implementation Steps:**
1.  **Docking Layout:** Install `Dock.Avalonia`. Define a default dock layout for the "Agent Sandbox" view containing three dockable panels: Terminal, Diff Viewer, and Staging Tree.
2.  **Activity Bar Grid:** In `ActivityBarView.axaml`, implement a 2-Row Grid.
3.  **Pinned Top:** Add the "Coordinator Tab" button to Row 0. Add a pulsing animation to this button (animating opacity/color) triggered by an `IsAttentionRequired` boolean in the ViewModel.
4.  **UI Virtualization:** In Row 1, use a `VirtualizingStackPanel` inside the `ItemsControl` bound to an `ObservableCollection<WorkerAgentViewModel>` to prevent layout avalanches when spawning 50+ agents.
5.  **Micro-Badges:** Add an ellipse/badge to the worker tab data template. Bind its `Fill` property to an `AgentStatus` enum using an Avalonia value converter (Running -> Green, Awaiting -> Yellow, Conflict -> Red).
6.  **LIFO Insertion:** Ensure the Spawner method adds new workers via `ObservableCollection.Insert(0, newAgent)`.
7.  **Deterministic Teardown & Floating Window Leaks:** Use the MVVM Community Toolkit's `WeakReferenceMessenger` for global repository events. When a tab is closed, you must explicitly traverse the `IDock` layout factory and call `window.Close()` on any detached/floating `IWindow` views to prevent native window handle leaks. Then call `Dispose()` on the ViewModel and clear terminal buffers.

### 🤖 Instruction: Phase 7.5: Dual-Mode Orchestration
**Objective:** Implement the state machine that governs Manual vs. Coordinator interactions.
**Implementation Steps:**
1.  **Terminal Locking:** Bind the Avalonia Terminal's `IsReadOnly` property to an `IsCoordinatorManaged` boolean on the agent profile. If true, disable keyboard input and display a top banner: "🔒 Managed by Coordinator".
2.  **Coordinator API:** Build an internal API layer that allows the Main Coordinator Agent (e.g., an LLM with tool-calling capabilities) to invoke the `WorktreeManager.cs` directly to spawn its own sub-agents up to the `MaxSubagentsLimit`.
3.  **The Human Handoff (Approval Gate):** When the Coordinator verifies a worker's task is complete, it calls an internal API to flag the worker UI. This changes the worker's micro-badge to Yellow (Awaiting Review). The Coordinator *does not* execute a merge.
4.  **Foreground Conflict Resolution:** The human developer reviews the worker's branch and manually clicks "Merge to Main" in the UI. If this foreground merge hits a conflict, GitLoom handles it exactly like a normal human Git conflict: it launches the 3-way `DiffViewerControl` for the user to resolve. The Coordinator is completely unaffected because it was not involved in the merge execution.

---

# GitLoom Velopack & OOBE Implementation Details

This document outlines the technical implementation for deploying GitLoom via **Velopack** and building the "First-Run" Out of Box Experience (OOBE) directly into the Avalonia application. This approach ensures a unified, beautiful setup UI across Windows, macOS, and Linux.

---

## 1. The Avalonia First-Run UI (OOBE)

Velopack extracts the application silently. When `GitLoom.exe` starts, it checks a local boolean `SetupComplete`. If false, it loads the OOBE View instead of the main IDE.

### A. The Vibe vs Dev Fork
*   **Implementation:** Screen 1 presents the choice. A global variable `INSTALL_MODE` is set to either `VIBE` or `DEV`.
*   **Dynamic Copy:** All subsequent screens bind their descriptive text to this variable. `VIBE` hides technical details behind "Sandbox Construction", while `DEV` surfaces logs and raw configuration states.

---

## 2. System Diagnostics & On-Demand Elevation

Because the setup UI is part of the main app, it runs without Administrator rights initially. It only requests Elevation (UAC/Sudo) when the user clicks "Construct Sandbox" to modify the OS.

### A. PowerShell Feature Checks (Windows)
*   **Implementation:** The installer executes a pre-flight check:
    ```powershell
    (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State
    ```
*   **Execution:** If the state is `Disabled`, the installer executes the `Enable-WindowsOptionalFeature` command.

### B. The `RunOnce` Reboot Handling
*   **Implementation:** Enabling the Virtual Machine Platform requires a restart. The installer writes its own execution path to `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce` with a `--resume` flag.
*   **Execution:** Post-reboot, the installer launches silently, detects the flag, and proceeds directly to Phase 3.

---

## 3. WSL Abstraction & `GitLoomOS` Import

This phase installs the actual nested sandbox architecture.

### A. The Tarball Payload (Contents & Usage)
*   **Implementation:** The installer payload contains the `GitLoomOS.tar.gz` file. 
*   **Contents:** This tarball is an exported Linux Root Filesystem (rootfs). It contains a barebones Debian or Alpine OS, pre-configured with `dockerd` (the Docker daemon), `git`, `python3`, and `node/npm`. It acts as the universal "Host OS" for all GitLoom projects.
*   **Usage:** It is designed to be ingested by the Windows Subsystem for Linux via the `wsl --import` command, which natively transforms the `.tar.gz` file into a bootable, private Virtual Machine instance attached to an `.ext4` virtual hard drive.

### B. The Silent Import
*   **Implementation:** The installer executes the import:
    ```bash
    wsl.exe --import GitLoomEnv "C:\Users\Target\AppData\Local\GitLoom\wsl" "C:\Program Files\GitLoom\GitLoomOS-minimal.tar.gz"
    ```

---

## 4. Windows OS Integration

The installer modifies the Windows Registry to bind GitLoom deeply into the host OS workflows.

### A. Context Menus
*   **Implementation:** Add keys to `HKEY_CLASSES_ROOT\Directory\shell\GitLoom`.
*   **Execution:** When a developer right-clicks a folder in Windows Explorer and selects "Open with GitLoom", the app launches and automatically provisions a persistent Docker container and synced clone inside `GitLoomEnv`.

### B. URL Protocol Handlers (OAuth Interception)
*   **Implementation:** Register `gitloom://` in `HKEY_CLASSES_ROOT\gitloom`.
*   **Execution:** Essential for Vibe Mode's headless authentication. When the backend agent generates an Auth URL (e.g., Anthropic login), the UI opens the browser. After successful login, the provider redirects to `gitloom://auth?token=xyz`. Windows automatically routes this back to `GitLoom.exe`, passing the token into the headless WSL backend seamlessly.

---

## 5. Agent Provisioning & Authentication (Post-Verify)

Only after the WSL instance is successfully imported and verified does the wizard prompt the user for Agent setup. This prevents wasted effort if hardware virtualization fails.

### A. Selection & Authentication
*   **Implementation:** The wizard UI presents checkboxes for supported CLIs (Claude Code, AGY, OpenCode). It immediately handles API Key input or browser OAuth flows.
*   **Dynamic Execution:** The installer executes `wsl -d GitLoomEnv --exec npm install -g @anthropic-ai/claude-code` (or equivalent) for the selected CLIs. This ensures the user gets the absolute latest version of the CLI. Finally, the installer injects the Auth Tokens into the respective CLI configuration files natively within the Linux filesystem.

---

## 6. Clean Teardown (The Uninstaller)

A critical component of the installer is the uninstaller. If a user removes GitLoom, it must not leave gigabytes of orphaned Docker containers in a hidden WSL instance.

*   **Implementation:** The Uninstaller sequence executes:
    ```bash
    wsl.exe --unregister GitLoomEnv
    ```
*   **Execution:** This single command entirely deletes the `GitLoomOS` Linux VM, the embedded Docker Engine, all persistent per-repo containers, and all `ext4` virtual drives, perfectly returning the user's hard drive to its original state.

---

# GitLoom Vibe Mode: Implementation Details

This document outlines the concrete technical implementation for "Vibe Mode".

**Architectural Law:** The underlying environment is 100% identical to Developer Mode. GitLoom still provisions a headless WSL2/DevContainer instance, still uses `Pty.Net` to natively run Agentic CLIs (Claude Code, AGY) on Linux, and still relies on `LibGit2Sharp` for Git control. The crucial difference is the introduction of the **`VibeOrchestrator`** component inside the backend `GitLoom.Server` daemon, which fully automates these mechanics.

---

## 1. The Unified Shared Environment

The setup process is identical for the entire GitLoom application, creating a seamless, zero-friction foundation regardless of whether the user prefers Dev Mode or Vibe Mode.

### A. The `GitLoomOS` Pre-Baked Sandbox
*   **Implementation:** The GitLoom Windows installer bundles a lightweight, pre-configured Linux tarball (e.g., Alpine or Ubuntu-minimal). On first launch, the app silently executes `wsl --import GitLoomEnv <path/to/tarball>`.
*   **Execution:** This guarantees a completely isolated, pristine Linux subsystem specifically for GitLoom. It bypasses any local Windows Node.js/Python configuration issues and ensures native `ext4` filesystem performance.
*   **Auto-Bootstrapper:** The `GitLoom.Server` daemon starts within this WSL instance, checks for the required CLI agents (`claude-code`, `agy`), and runs silent updates or installations as needed.

### B. Native CLI Authentication (No Mandatory API Keys)
*   **The Challenge:** Vibe Mode hides the terminal, so users cannot manually click OAuth login links generated by CLIs like Claude Code.
*   **Implementation:** The backend `Pty.Net` stream parser implements a Regex listener for OAuth URLs (e.g., `https://auth.anthropic.com/login?...`).
*   **Execution:** When the stream outputs an authentication link, the backend sends a gRPC `[AUTH_REQUIRED]` event containing the URL to the Avalonia Client. The client automatically pops open the URL in the user's default Windows browser. Once the user authenticates in the browser, the CLI agent sitting in the WSL background automatically resumes, and the UI transitions to the active workspace. API keys remain strictly optional.

### C. Dev Server Port Harvesting
*   **Implementation:** When an agent runs `npm run dev`, the `Pty.Net` stream parser detects the `http://localhost:([0-9]+)` pattern and emits an event to both the internal `VibeOrchestrator` and the Avalonia Client.

---

## 2. The Backend `VibeOrchestrator` Layer

Instead of the frontend acting as the puppet master and trying to translate Git concepts over a network boundary, the `GitLoom.Server` introduces the `VibeOrchestrator` service to act as a "virtual developer".

### A. In-Memory Stream Routing (Auto-Healing)
*   **Implementation:** The Orchestrator natively hooks into the `Pty.Net` streams of both the Dev Server and the Agent CLI (Claude/AGY) entirely in memory on the Linux server.
*   **Execution:** If the Dev Server outputs `ERR!` or a stack trace, the Orchestrator instantly captures it. Without sending a single byte of that stack trace to the Windows UI, it writes the error directly into the `StandardInput` of the Agent CLI process with the appended prompt: *"The dev server crashed. Fix this."*
*   **Resiliency:** Because this loop runs in the backend, the user can completely close the GitLoom UI on Windows, and the agent will continue fixing bugs in WSL.

### B. Autonomous Git Abstraction (GAL)
The Orchestrator translates Vibe Mode intents into standard `LibGit2Sharp` backend calls automatically.
*   **Auto-Checkpoints:** When the Orchestrator detects the agent CLI has completed its generation loop successfully, it invokes `GitService.StageAll()` and `GitService.Commit(author, "Auto-Checkpoint")` directly on the local repository.
*   **Autonomous Conflict Resolution:** If an automated merge throws a `MergeConflictException`, the Orchestrator catches it. It feeds the conflict markers into the Agent CLI with a system prompt: *"A merge conflict occurred. Read the conflict markers and resolve them."* The agent uses its own file-editing tools to fix the code, and the Orchestrator finalizes the merge.

---

## 3. The "Dumb" Vibe Mode UI

Because the `VibeOrchestrator` handles all complexity, the Avalonia UI becomes incredibly lightweight and resilient.

### A. The Vibe Mode Layout Toggle
*   When "Vibe Mode" is active, the `AgentSandboxViewModel` forcefully hides the `TerminalViewModel`, `DiffViewerControl`, and `StagingTree`. 
*   The layout is locked into a 2-pane vertical split: The Chat Interface (Left) and the Embedded Web Browser (Right).

### B. Chat-to-Orchestrator Bridge
*   **Implementation:** The user's chat input is sent via gRPC to the `VibeOrchestrator`. The Orchestrator pipes this into the Agent CLI's input stream.
*   **Event Subscription:** The frontend no longer parses raw ANSI streams for errors. Instead, it subscribes to clean, high-level gRPC events emitted by the Orchestrator, such as `[Event: Checkpoint_Created]`, `[Event: Auto_Fixing_Error]`, and `[Event: Agent_Replied_With_Text]`.

### C. The Embedded Live Preview
*   **Implementation:** The Avalonia Client implements a `LivePreviewControl` (`WebView2`/`CefGlue`). Upon receiving the `[APP_READY_ON_PORT_X]` event, the control navigates the embedded browser to the local URL. Frame hot-reloading works natively since the backend and frontend share the same local volume mapping.
