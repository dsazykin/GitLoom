# GitLoom Multi-Agent Control Center: Implementation Details

This document outlines the concrete implementation specifications for integrating concurrent AI CLI agents (Claude Code, AGY, OpenCode) into the GitLoom architecture using native OS terminals and Git worktrees.

---

## 1. The Environment: The Client-Server Split Architecture

GitLoom abandons Docker Desktop for Windows and avoids the notorious 9P file share latency. Instead, it relies on a **Containerized Git Sandbox Architecture** for zero-friction setup, maximum security, and blazing execution speed.
*   **The Nested Engine (WSL2 -> Raw Docker):** The GitLoom Windows installer silently provisions a private, lightweight WSL2 instance (`GitLoomOS`). Inside this native Linux boundary, GitLoom runs the raw, open-source Docker Engine (`dockerd`) in the background.
*   **Persistent Per-Repo Isolation (Blast Radius Protection):** When a user opens a repository, `GitLoom.Server` creates a dedicated, persistent Docker container. The Agentic CLIs are completely jailed within this container, preserving `node_modules` caches and agent sessions across app restarts.
*   **The "Hollow-Core" Architecture (Selective I/O Offloading):** To avoid 9P file latency without breaking uncommitted pair-programming, GitLoom uses a hybrid mount. The repository remains entirely on `C:\Code\Project` and is bind-mounted natively into the container (`-v /mnt/c/Code/Project:/workspace`). However, to prevent massive 9P latency during heavy tasks (`npm install`), GitLoom dynamically provisions native Linux `ext4` Docker volumes and mounts them *over* the heavy directories (e.g., `/workspace/node_modules`).
*   **The Remote IDE & Post-Merge Sync:** To avoid the CPU overhead of syncing 150,000 `node_modules` files back to Windows for IntelliSense, GitLoom relies on **VS Code Remote Attach** for code review (reading the Linux volume natively). Upon clicking "Approve & Merge", GitLoom automatically executes `git merge` and `npm install` on the Windows host. If rejected, GitLoom automatically executes `npm prune` inside the container.

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
*   **Memory Management:** The MVVM architecture enforces the Weak Event Pattern (`WeakReferenceMessenger`) and deterministic teardown (`Dispose()` on tab close, explicitly halting `DispatcherTimer` and disposing `WebView2` instances) to prevent dangling Skia visual trees and memory leaks.
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
5.  **Environment Injection (tmpfs):** When spawning agents via `IAgentExecutor`, decrypt the key in-memory on the Windows side. Do NOT send the plaintext key over localhost gRPC. Pass it securely to a localized pipe, and inject it strictly into a `tmpfs` RAM disk volume mounted to the container (`--mount type=tmpfs,destination=/dev/shm`). Write to `/dev/shm/.env` so secrets never touch a physical `ext4` block device or `ps aux` command arguments.

### 🤖 Instruction: Phase 7.1: The JetBrains Terminal Engine (`Pty.Net`)
**Objective:** Replace standard process redirection with a native Pseudo-Terminal to ensure CLI tools function without `isatty()` crashes.
**Implementation Steps:**
1.  **Backend Dependency:** Install the `Pty.Net` NuGet package in `GitLoom.Core`.
2.  **Process Spawner:** Build `PtyProcessShim.cs` that invokes `PtyProvider.SpawnAsync()`. Pass `PtyOptions` configuring the `App` (e.g., `npx claude`), the arguments, and crucially, set `Cwd` to the isolated worktree directory.
3.  **Frontend Binding:** In `GitLoom.App`, integrate the `Iciclecreek.Avalonia.Terminal` control (or `XTerm.NET`).
4.  **Stream Piping & VT100 Stateful Throttling:** The `GitLoom.Server` collects `Pty.Net` bytes and flushes them across gRPC every 16ms (60 FPS). Crucially, a stateful VT boundary detector prevents the 16ms tick from cleaving multi-byte ANSI sequences (e.g., color codes) in half. If the tick lands mid-sequence, it buffers the remainder for the next frame, ensuring animations and colors render flawlessly without crashing the Avalonia parser.
5.  **Memory Guard:** Implement a strict circular-overwrite scrollback limit (e.g., 10,000 lines) on the terminal's backing model.

### 🤖 Instruction: Phase 7.2: The Containerized Sandbox & Git Fetch Bridge
**Objective:** Securely isolate agents per-repository using Docker, and then isolate concurrent agent threads using Git Worktrees, completely abandoning Docker Desktop and 9P volume mounts.
**Implementation Steps:**
1.  **The `GitLoomOS` Bootstrapper:** On first launch, the Windows App executes `wsl --import GitLoomEnv` using a bundled minimal Linux tarball. It then starts `dockerd` inside this WSL instance via a background daemon.
2.  **Persistent Container Lifecycle & Auth Mounting:** When a project is loaded, check if its container exists. If not, GitLoom executes `docker create`. Crucially, it mounts the global `GitLoomOS` authentication directories as read-write volumes (e.g., `-v ~/.claude:/root/.claude:rw`). This allows Headless OAuth flows inside the container to persist tokens back to the host. Then `docker start` is executed.
3.  **The "Hollow-Core" Mount Manager:** Bind-mount the Windows host path directly into the container (`-v /mnt/c/Code/Project:/workspace`). Then, dynamically create native Linux `ext4` Docker anonymous volumes and mount them *over* the heavy directories (e.g., `-v /workspace/node_modules`, `-v /workspace/dist`).
4.  **Dynamic Toolchain Sideloading:** If the repository lacks a `.devcontainer`, do NOT run `docker build` at runtime, as this destroys the container and severs the active PTY session. Instead, use a static base Docker image equipped with a dynamic package manager like Nix or Devbox. Execute `devbox add <package>` inside the active container to install toolchains on the fly without restarting.
5.  **Worktree Manager & PNPM Hardlinking:** Create `WorktreeManager.cs`. Execute `git worktree add ../agent_workspace/agent_{id} agent/{id}` to physically separate concurrent agent edits. Because `node_modules` is ignored by Git, it is missing in the new worktree. Execute `pnpm install` immediately. `pnpm` will use its global content-addressable Linux store to instantly **hardlink** dependencies into the worktree's `node_modules`, ensuring 50 agents take up the disk space of 1 agent.
6.  **Zombie Swarm Prevention:** On launch, do NOT rely on static lockfiles (which suffer from PID recycling). The `.NET` daemon must interrogate `/var/run/docker.sock` directly via the `Docker.DotNet` SDK. Docker is the sole cryptographic source of truth for container lifecycle. If the container is dead, execute `git worktree prune`.
7.  **PTY Routing & Execution:** The `GitLoom.Server` executes `docker exec -e DOTENV_CONFIG_PATH=/dev/shm/.env -it <container_id> <agent_cli>` to spawn the agent, explicitly routing it to the volatile `tmpfs` RAM disk for API keys. The `Pty.Net` streams capture the container output and pipe it over gRPC to the Windows Avalonia UI.
8.  **Git Fsmonitor Shim:** Because the repository is mounted over 9P, `git status` commands can be slow due to thousands of `stat()` calls. To mask this latency, automatically enable `core.fsmonitor = true` and `core.untrackedCache = true` inside the container's global `.gitconfig` to utilize the background caching daemon.
9.  **AF_VSOCK gRPC Communication & TCP Tunneling:** The `GitLoom.Server` daemon binds strictly to the Hyper-V Socket (`AF_VSOCK`) interface rather than standard TCP/IP to prevent VPN intercepts and dynamic IP changes. Furthermore, the daemon implements a TCP tunnel over this gRPC connection to forward HTTP traffic from the container's isolated Dev Server back to the Windows UI without relying on fragile WSL NAT bridging.

### 🤖 Instruction: Phase 7.3: The Agent Lifecycle & Merging Workflow
**Objective:** Manage the lifecycle of agent worktrees, including cross-agent code sharing and pristine teardowns.
**Implementation Steps:**
1.  **State Failsafes & Process Suspension:** Before executing Git commands, check for `.git/worktrees/<id>/rebase-merge/` or detached HEAD; abort if found. Implement the **Cooperative Yield Protocol**. The daemon sends `[IPC_UPDATE_REQUESTED]` and waits asynchronously for `[IPC_UPDATE_READY]`. This stateless triad avoids 5-second timeout race conditions that would brick blocked agents. Wrap successful Git operations in exponential backoff retry loops for `index.lock` errors.
2.  **The Middle Manager Lifecycle (Foreground Integration):** Prevent Working Directory Poisoning (injecting conflicts into the user's active IDE) by relying on foreground merges.
    *   **Keep-Alive Rebase (Sync only):** Suspend the agent. Inside the agent's worktree, stage, commit, and execute `git rebase main` (referencing without checkout). Resume.
    *   **Awaiting Human Review:** The Coordinator agent calls an internal API to flag the worker UI. The worker pauses indefinitely.
    *   **Remote IDE Review:** GitLoom surfaces a "Review in IDE" button that launches `code --folder-uri vscode-remote://attached-container+<hash>/workspace`. This provides perfect IntelliSense by reading the native Linux `node_modules` volume without copying files to Windows.
    *   **Foreground Merge (User Action):** When the human clicks "Merge to Main", GitLoom executes `git merge agent/{id}` on the Primary Repository. Immediately after merging, GitLoom spawns a background process on the Windows host to execute `npm install`, silently keeping the user's local dependencies synchronized.
    *   **Rejection Cleanup:** If the human clicks "Reject", GitLoom deletes the agent's branch. The container's `package.json` reverts to `main`. GitLoom immediately runs `npm prune` inside the container to cleanly uninstall any orphaned packages from the Linux `node_modules` volume.
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
7.  **Deterministic Teardown & Floating Window Leaks:** Use the MVVM Community Toolkit's `WeakReferenceMessenger` for global repository events. When a tab is closed, you must explicitly traverse the `IDock` layout factory and call `window.Close()` on any detached/floating `IWindow` views to prevent native window handle leaks. In the ViewModel's `Deactivate` hook, explicitly halt the 60FPS `DispatcherTimer`, call `WebView2.Dispose()`, and clear terminal buffers to prevent severe memory leaks.

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
*   **Implementation:** The installer executes a pre-flight check by querying WMI (`Win32_ComputerSystem`) to verify VT-x/AMD-V hardware virtualization is enabled. If verified, it checks the feature state:
    ```powershell
    (Get-WindowsOptionalFeature -Online -FeatureName VirtualMachinePlatform).State
    ```
*   **Execution:** If the state is `Disabled`, the installer executes the `Enable-WindowsOptionalFeature` command.

### B. The Elevated Scheduled Task Reboot Handling
*   **Implementation:** Enabling the Virtual Machine Platform requires a restart. The installer creates a high-privilege Windows Scheduled Task (rather than a `RunOnce` key which drops privileges) with a `--resume` flag.
*   **Execution:** Post-reboot, the installer launches silently with necessary elevation, detects the flag, and proceeds directly to Phase 3.

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

*   **Implementation:** The Uninstaller sequence executes a hard terminate to clear open handles:
    ```bash
    wsl.exe --terminate GitLoomEnv
    ```
*   **Execution:** Implement a programmatic polling loop checking `wsl.exe -l -v` until the state definitively reports "Stopped" to ensure all `.vhdx` locks are released. Then execute `wsl.exe --unregister GitLoomEnv`. This entirely deletes the `GitLoomOS` Linux VM, the embedded Docker Engine, and all `ext4` virtual drives, perfectly returning the user's hard drive to its original state. Do NOT use `wsl --shutdown` as it ruthlessly kills all of the user's personal WSL instances.

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
*   **Terminal Subscription:** Vibe Mode explicitly relies on the standard raw PTY stream (rendered by `Avalonia.Terminal`) to preserve native CLI animations, loading spinners, and colors. No ANSI stripping or `--json` event abstraction is required for the user interface.

### C. The Embedded Live Preview
*   **Implementation:** The Avalonia Client implements a `LivePreviewControl` (`WebView2`/`CefGlue`). Upon receiving the `[APP_READY_ON_PORT_X]` event, the control navigates the embedded browser to the tunneled local URL (mapped via the gRPC TCP tunnel). Frame hot-reloading works natively and instantly since the Windows IDE and the Docker container are reading the exact same Windows path via the Hollow-Core bind mount.
