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
- **Staging, Diffs, & Committing:** Side-by-side or unified diff viewing, staged committing, and remote network sync (Push/Pull/Ahead/Behind tracking).
- **Partial Staging:** Stage, unstage, or discard individual hunks — or drag-select individual lines in the unified view, or accept/discard whole blocks in the side-by-side view — all driven by a pure patch engine (`PatchParser`/`PatchBuilder`) validated against `git apply`.
- **Conflict Resolution:** A synchronized 3-pane merge editor (Ours | Result | Theirs) with per-side accept/reject/undo, built on a pure 3-way merge chunker; merge/rebase/cherry-pick/pull all route conflicts here.
- **Branch, Tag & Worktree Management:** Branch interactions with checkout safety validation and stashing; full tag lifecycle (create lightweight/annotated, push/delete-remote, checkout, graph chips); and git-worktree porcelain (list/add/remove/prune).
- **Switchable UI Themes:** A tokenized design system with five color schemes — Midnight Loom (default), Daylight Loom (light), Command Deck, Atelier, and Loom Aurora — switchable live from File → Theme and persisted across sessions.

---

## 🔮 Future Vision & "How It Works" (Roadmap)
The following features are actively being engineered to transform GitLoom into the ultimate Multi-Agent Control Center.

### 1. Seamless Synchronous Collaboration (The "Middle Manager")
**Code side-by-side with your swarm in perfect harmony.** 
GitLoom’s planned "Middle Manager Architecture" synchronizes state between the human and the agents. You can actively code a feature in your IDE on the `main` branch, while Agent A builds a database schema and Agent B designs a frontend component. GitLoom's background daemon will handle "Keep-Alive" rebases to ensure agents safely inherit your latest saves without `.git/index.lock` collisions.

### 2. True, Conflict-Free Concurrency (Docker Sandboxes)
**Never let an AI break your working directory again.**
Instead of agents editing files live in your IDE, GitLoom will jail every agent in its own persistent Docker Sandbox (`sbx`) microVM and isolated Git worktree. They can write code, run dev servers, and make mistakes completely independently in a hardware-isolated environment. To prevent silent breakages, GitLoom automatically runs **Semantic Conflict Verification** (executing your test suite inside the container) to ensure the code is functionally sound before it reaches you. You review their branches securely in the foreground and click "Merge" only when you're happy.

### 3. Flawless Environment Setup (The No-Mount Clone)
GitLoom will bypass Docker Desktop and Windows-to-WSL 9P volume latency. It will silently install a lightweight `GitLoomOS` (Linux) in the background. Agents get perfect Node/Python environments natively on a Linux Docker volume, while you enjoy a native Windows Avalonia UI. 

### 4. JetBrains-Grade Native Terminals
Many modern dev tools rely on sluggish web-based terminals (`xterm.js`) embedded in Electron apps. GitLoom will integrate real OS-level pseudo-terminals (`Pty.Net` via ConPTY/forkpty) rendered with Skia. Interactive CLIs, curses interfaces, and fast-scrolling logs will work flawlessly without dropping keystrokes.

### 5. "Vibe Mode" (Zero-Knowledge Abstraction)
Vibe Mode introduces a backend "Virtual Developer." This autonomous orchestrator will intercept stack traces from dev servers entirely in-memory, feed them back to the AI for auto-healing, and handle Git conflicts automatically. Perfect for designers and founders who want results without ever seeing a terminal.

### 6. Enterprise AI Governance (B2B Audit Trails)
To satisfy enterprise SOC2 requirements, GitLoom acts as a tamper-evident audit trail. GitLoom is 100% closed-source but provides a **Zero-Exfiltration Guarantee** (all LLM APIs are BYOK and connect directly to providers, bypassing any GitLoom cloud). Every prompt, model inference, and human-in-the-loop intervention is cryptographically logged locally and can be streamed directly to enterprise SIEM platforms.

### 7. Supported Agents & Integrations
Out-of-the-box support is planned for leading agentic CLIs including **Claude Code**, **AGY**, and **OpenCode**. GitLoom will manage their API keys or browser OAuth securely via the native OS keyring (DPAPI/Keychain/Secret Service).

---

## 🛠️ Installation & Getting Started

### Current Installation (Developer Preview)
Currently, GitLoom must be built from source using the .NET 10.0 SDK.
1. Clone the repository.
2. Run `dotnet restore`.
3. Run `dotnet build` or open `GitLoom.slnx` in your preferred IDE (Visual Studio / Rider).
4. Launch the `GitLoom.App` project.

The SDK version is pinned in `global.json`, so `dotnet` automatically uses the correct toolchain. Contribution rules, project layout, and conventions live in [`AGENTS.md`](AGENTS.md).

### Containerized Build & Test (Optional)
A Docker image is provided that reproduces the exact `.NET 10` build/test toolchain (plus the native `LibGit2Sharp` / `SkiaSharp` dependencies) so builds and tests run identically on any machine — no local SDK required.

> **Note:** the container is for **building, testing, and EF migrations only** — *not* for running the desktop GUI. End-user distribution stays native per-OS (see Velopack below).

The `docker compose` wrappers bind-mount your working tree (host edits are picked up without rebuilding) and cache NuGet packages between runs:

```bash
docker compose run --rm build     # restore + build the whole solution
docker compose run --rm test      # run all test suites headlessly
docker compose run --rm shell     # interactive shell in the toolchain (e.g. dotnet ef ...)
```

The first run builds the image; subsequent runs reuse it. See `Dockerfile` and `docker-compose.yml` for details.

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
* [Technical Roadmap & Architecture Blueprint](docs/planning/GitLoom_Roadmap.md)
* [Implementation Plan](docs/planning/Implementation_Plan.md)
