# GitLoom

**A Premium Git GUI evolving into a Multi-Agent Control Center**

![GitLoom Screenshot]()

## Why Build This? (The Philosophy)
Current AI IDE extensions are fantastic for single-file edits or quick auto-completions. However, when you need to build entire features, you need autonomous Agentic CLIs (like Claude Code, AGY, or OpenCode). 

Managing multiple autonomous CLIs in split terminals quickly becomes a nightmare. They step on each other's toes, overwrite human work, and cause Git lock conflicts. **GitLoom** is being built to solve this. It elevates you from writing code to an "Engineering Manager," orchestrating a swarm of AI workers from a single, beautiful command-and-control dashboard.

---

## 🚀 Currently Implemented Features
GitLoom is currently in active development. The foundation has been built as a blazing-fast, natively rendered Git client using Avalonia UI and `LibGit2Sharp`.

- **High-Performance Commit History & Graph:** Features an isolated DAG lane-routing engine and a virtualized vector canvas (`CommitGraphCanvas`) that effortlessly renders complex Git histories at 60 FPS.
- **Advanced Workspace Manager:** Includes a debounced `FileSystemWatcher` targeting `.git/refs` and `.git/index` for instant UI updates without I/O bursts, backed by a local SQLite bookmark store.
- **Staging, Diffs, & Committing:** Built-in side-by-side or unified plain-text diff viewing, staged committing, and remote network sync (Push/Pull/Ahead/Behind tracking).
- **Branch Management:** Deeply nested UI architecture for branch interactions, checkout safety validation, and stashing.

---

## 🔮 Future Vision & "How It Works" (Roadmap)
The following features are actively being engineered to transform GitLoom into the ultimate Multi-Agent Control Center.

### 1. Seamless Synchronous Collaboration (The "Middle Manager")
**Code side-by-side with your swarm in perfect harmony.** 
GitLoom’s planned "Middle Manager Architecture" synchronizes state between the human and the agents. You can actively code a feature in your IDE on the `main` branch, while Agent A builds a database schema and Agent B designs a frontend component. GitLoom's background daemon will handle "Keep-Alive" rebases to ensure agents safely inherit your latest saves without `.git/index.lock` collisions.

### 2. True, Conflict-Free Concurrency (Containerized Sandboxes)
**Never let an AI break your working directory again.**
Instead of agents editing files live in your IDE, GitLoom will jail every agent in its own persistent Docker container and isolated Git worktree. They can write code, run dev servers, and make mistakes completely independently. You review their branches securely in the foreground and click "Merge" only when you're happy.

### 3. Flawless Environment Setup (The No-Mount Clone)
GitLoom will bypass Docker Desktop and Windows-to-WSL 9P volume latency. It will silently install a lightweight `GitLoomOS` (Linux) in the background. Agents get perfect Node/Python environments natively on a Linux Docker volume, while you enjoy a native Windows Avalonia UI. 

### 4. JetBrains-Grade Native Terminals
Many modern dev tools rely on sluggish web-based terminals (`xterm.js`) embedded in Electron apps. GitLoom will integrate real OS-level pseudo-terminals (`Pty.Net` via ConPTY/forkpty) rendered with Skia. Interactive CLIs, curses interfaces, and fast-scrolling logs will work flawlessly without dropping keystrokes.

### 5. "Vibe Mode" (Zero-Knowledge Abstraction)
Vibe Mode introduces a backend "Virtual Developer." This autonomous orchestrator will intercept stack traces from dev servers entirely in-memory, feed them back to the AI for auto-healing, and handle Git conflicts automatically. Perfect for designers and founders who want results without ever seeing a terminal.

### 6. Supported Agents & Integrations
Out-of-the-box support is planned for leading agentic CLIs including **Claude Code**, **AGY**, and **OpenCode**. GitLoom will manage their API keys or browser OAuth securely via the native OS keyring (DPAPI/Keychain/Secret Service).

---

## 🛠️ Installation & Getting Started

### Current Installation (Developer Preview)
Currently, GitLoom must be built from source using the .NET 10.0 SDK.
1. Clone the repository.
2. Run `dotnet restore`.
3. Run `dotnet build` or open `GitLoom.slnx` in your preferred IDE (Visual Studio / Rider).
4. Launch the `GitLoom.App` project.

### Future Distribution (The Velopack OOBE)
In the future, GitLoom will utilize **Velopack** for zero-friction cross-platform distribution (Windows `.exe`, macOS `.dmg`, Linux `.AppImage`). 
There will be no clunky installers. A single executable will silently provision the `GitLoomOS` WSL instance, handle hardware diagnostics, and present a beautiful Out of Box Experience (OOBE) First-Run Wizard right inside the Avalonia UI.

---

## 📚 Under the Hood
- **Desktop Framework:** Avalonia UI (v11.1.3 - Stable)
- **MVVM Engine:** `CommunityToolkit.Mvvm` (v8.4.2)
- **Git Engine:** `LibGit2Sharp` (v0.30.0+)
- **Local Database:** SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)

For an in-depth understanding of GitLoom's architecture, swarm mechanics, and implementation details, refer to:
* [Technical Roadmap & Architecture Blueprint](GitLoom_Roadmap.md)
* [Implementation Plan](Implementation_Plan.md)
