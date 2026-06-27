# GitLoom: Technical Roadmap & Architecture Blueprint

GitLoom is a premium, cross-platform desktop **Git GUI & Agentic Control Center** built natively in C# and Avalonia UI. It serves as a beautiful, high-performance, and secure dashboard where developers can manage their Git version control workflow and execute autonomous AI agents (like Claude Code, AGY CLI, and OpenCode) in isolated Docker and WSL sandboxes.

---

## 1. Core Vision & Architectural Goals

- **Zero-Risk Agent Execution:** Fully isolates agent actions from the host system using Docker containers and Windows Job Objects.
- **The "Agent PR" Workflow:** Severs host OS file-locking dependencies entirely. Agents operate on isolated Linux ext4 working trees and synchronize changes back to the host via Git packfiles.
- **Zero Cloud Friction:** Maintains an offline-first foundation. Cloud interactions are strictly user-driven.
- **Double-Layer Optimization:**
  - **Git Engine:** LibGit2Sharp (compiled C-bindings) handles indexing and diff parsing natively on Host NTFS paths with instantaneous speeds.
  - **Metadata Engine:** Uses a local SQLite database to store repository bookmarks and Named Docker Volume manifests.
  - **UI Responsiveness:** Bounded `System.Threading.Channels` coupled with strict Skia viewport virtualization prevents HarfBuzz text-shaping CPU meltdowns.

---

## 2. Technical Stack & Dependencies

- **Desktop Framework:** Avalonia UI (v11.1.3 - Stable)
- **MVVM Engine:** `CommunityToolkit.Mvvm` (v8.4.2)
- **Git Engine:** `LibGit2Sharp` (v0.30.0+ - standard native libgit2 bindings)
- **Local Database:** SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Data Visualization:** `LiveChartsCore.SkiaSharpView.Avalonia` (v2.0.4)
- **Sandbox Engine:** Docker Desktop (WSL2 backend on Windows) / WSL (Direct Host fallback)
- **Terminal Control:** `Iciclecreek.Avalonia.Terminal` / `XTerm.NET` wrapper

---

## 3. Project Structure

```text
GitLoom/
├── GitLoom.slnx                        # Solution map
├── GitLoom.Core/                       # Core engine, database model context, and business logic
│   ├── GitLoom.Core.csproj
│   ├── AppDbContext.cs                 # Entity Framework Core SQLite DB context
│   ├── Models/                         # Core domain models
│   ├── Services/                       # Domain business logic wrappers
│   ├── Graph/                          # History visualization DAG logic
│   ├── Security/                       # Credentials storage logic
│   ├── Sync/                           # Remote server communication logic
│   ├── Analytics/                      # Repository analyzer metrics code
│   └── Agents/                         # Isolated Agent Sandbox runner [PLANNED]
│       ├── IAgentExecutor.cs           # Execution abstraction interface [PLANNED]
│       ├── DockerAgentExecutor.cs      # Docker sandbox container execution [PLANNED]
│       └── HostAgentExecutor.cs        # Direct OS / WSL shell execution [PLANNED]
│
├── GitLoom.App/                        # Avalonia UI GUI desktop application
│   ├── GitLoom.App.csproj
│   ├── ViewModels/                     # MVVM presentation logic
│   │   ├── AgentSandboxViewModel.cs    # Terminal dock & setup wizard logic [PLANNED]
│   │   └── AgentProfileSettingsViewModel.cs # Named Volume explorer config editor [PLANNED]
│   └── Views/                          # UI View layouts (XAML markup)
│       ├── AgentSandboxView.axaml      # bottom PTY terminal tab dock view [PLANNED]
│       └── AgentProfileSettingsView.axaml # Config editor tree & wizard view [PLANNED]
│
└── GitLoom.Tests/                      # xUnit unit testing suites
```

---

## 4. Phase-by-Phase Implementation Plan

### 🚀 Phase 1: Scaffolding & Workspace Manager (COMPLETED)
### 🛠️ Phase 2: Staging, Diffs, & Committing (COMPLETED)
### 🧬 Phase 3: High-Performance Commit History & Graph (COMPLETED)
### 🌿 Phase 4: Branch Management & Interactive Merging (IN PROGRESS)
* **Phase 4.4: In-App Code Editor & Conflict Resolution**

### 📊 Phase 5: Repository Analytics & Churn (Premium Polish)
### ☁️ Phase 6: Agent Profiles & Secure Keyring Sync (Opt-In Extension)

### 🤖 Phase 7: Integrated Agentic Control Center & Docker Sandbox
* **Phase 7.1: Dual-Mode Runtime & Process Hardening (`IAgentExecutor`)**
  - Implement dynamic `IAgentExecutor` targeting Docker containers or Direct OS shells.
  - Mitigate Docker Zombie leaks via boot-time sweeping `docker rm -f` against tagged labels (`--label gitloom.session=active`). Use Windows Job Objects strictly for host process annihilation.
  - Optimize Terminal streams utilizing bounded `System.Threading.Channels` and strict Skia viewport virtualization.
* **Phase 7.2: The Git Synchronization Engine (Solving 9P I/O Penalty)**
  - Execute a bare Git clone into a native Linux ext4 volume. The agent clones from the bare repo, performs massive I/O read/write operations at native speeds, and pushes the completed work back to the host via `git push origin HEAD:refs/gitloom/agent-pr`.
* **Phase 7.3: Application-Aware Configuration Backups & Setup Wizard**
  - Fix stranded dirty RAM pages by replacing `SIGSTOP` with a sidecar container using `--volumes-from` that natively executes `sqlite3 "VACUUM INTO 'backup.db'"`.
  - Achieve True Atomic Swaps by flushing archives to a `.tmp` file and executing a locked `File.Move` rename, avoiding Defender IOExceptions.
* **Phase 7.4: Agent PR Dashboard & IPC Security**
  - Secure Named Pipes on Windows by leveraging `PipeOptions.CurrentUserOnly` (.NET 8+) to strictly lock the Access Control List to the logged-in developer's SID.
  - Fix Docker IPC secret leakage by avoiding Environment Variables. Inject the token via an in-memory `tmpfs` volume (`/dev/shm/gitloom_ipc.key`), ensuring the agent shim reads and immediately executes `rm` to permanently erase the token from the container before spawning the untrusted CLI.
  - Enforce structured JSON heartbeats (`{"status": "busy", "task": "compiling"}`) from the shim over the IPC pipe to detect lockups instead of relying on OS I/O metrics.

---

## 5. Premium Design Token Specifications

| Token Key | HEX / HSL Value | Purpose |
| :--- | :--- | :--- |
| `BgObsidian` | `#0C0F12` | Solid background, deep base |
| `PanelSurface` | `#14191F` | Solid panels, primary widgets |
| `BorderGlow` | `rgba(255, 255, 255, 0.12)` | Clean 1.5px glowing borders |
| `TextWhite` | `#FFFFFF` | Primary titles, bold text |
| `TextMuted` | `#A6ADC8` | secondary details, dates, author names |
| `BranchCyan` | `#89B4FA` / HSL Blue | Cyan path for `main` or active branch |
| `BranchPink` | `#F5C2E7` / HSL Pink | Pink path for feature branches |
| `BranchGreen` | `#A6E3A1` / HSL Green | Staged badges, green diff additions |
| `BranchRed` | `#F38BA8` / HSL Red | Deleted files, red diff deletions |

---

## 6. Next Steps & Active Checklist

- [x] **Step 1:** Create new solution folder, initialize C# projects (`Core`, `App`, `Tests`).
- [x] **Step 2:** Reference package dependencies.
- [x] **Step 3:** Implement SQLite database setup and folder bookmarks metadata models.
- [ ] **Step 4:** Build the Repository Browser Sidebar shell.
