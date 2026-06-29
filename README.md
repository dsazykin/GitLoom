# GitLoom

GitLoom is a premium, cross-platform desktop **Git GUI & Multi-Agent Control Center** built natively in C# and Avalonia UI. It serves as a command-and-control dashboard where developers manage complex Git workflows alongside a swarm of autonomous AI agents (Claude Code, AGY CLI, OpenCode) running concurrently in isolated Git worktrees.

![GitLoom Screenshot]()

## Core Vision & Architectural Goals

- **The Swarm Coordinator:** A dual-mode orchestration system allowing you to either manually pilot multiple agents, or delegate tasks to a "Lead Agent" that automatically spawns and manages worker sub-agents.
- **Containerized Git Sandbox:** GitLoom bypasses Docker Desktop and 9P volume mount latency. It runs the raw open-source Docker Engine (`dockerd`) within a silent `GitLoomOS` WSL2 instance. Each repository gets a persistent, dedicated Docker container to jail agents and cache dependencies.
- **Zero-Conflict Concurrency via Git Sync:** Agents work on private Git branches within their isolated containerized clones. GitLoom's daemon automatically executes a `git fetch` to bridge the container's clone back to the Windows repository, allowing the user to seamlessly review and merge the agent's code without file-locking collisions.
- **Native OS Terminals:** GitLoom uses native OS pseudo-terminals (`Pty.Net` via ConPTY/forkpty) rendered via Skia, rejecting slow web-based terminal emulators, for robust and flawless CLI interaction.
- **Vibe Mode:** An autonomous mode designed for non-developers, where a backend `VibeOrchestrator` acts as a virtual user, driving terminals, resolving conflicts, and managing commits automatically.

## Technical Stack & Dependencies

- **Desktop Framework:** Avalonia UI (v11.1.3 - Stable)
- **MVVM Engine:** `CommunityToolkit.Mvvm` (v8.4.2)
- **Git Engine:** `LibGit2Sharp` (v0.30.0+)
- **Local Database:** SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Windowing/Layout:** `Dock.Avalonia`
- **Terminal Backend:** `Pty.Net`
- **Terminal Frontend:** `Iciclecreek.Avalonia.Terminal`

## Project Structure

```text
GitLoom/
├── GitLoom.Core/                       # Backend core logic
│   ├── AppDbContext.cs                 # SQLite EF Core database context
│   ├── Services/                       # GitService, Graph mapping, etc.
│   └── Agents/                         # Swarm Sandbox Engine (WorktreeManager, PtyProcessShim)
├── GitLoom.App/                        # Avalonia MVVM application
│   ├── ViewModels/                     # ActivityBarViewModel, TerminalViewModel, etc.
│   └── Views/                          # ActivityBarView, AgentSandboxView, etc.
└── GitLoom.Tests/                      # xUnit test suite for validating core logic
```

## Setup & Distribution

GitLoom utilizes **Velopack** for zero-friction cross-platform distribution (Windows `.exe`, macOS `.dmg`, Linux `.AppImage`). Instead of a separate installer, Velopack silently drops the binaries onto the system and launches GitLoom.

The very first launch provides an **Out of Box Experience (OOBE) First-Run Wizard** built into the Avalonia UI, asking whether you want to proceed in "Vibe Coder" or "Developer" mode, dynamically handling system hardware diagnostics and WSL installation.

### Vibe Mode vs. Developer Mode

* **Developer Mode:** Designed to operate as an Air Traffic Control center. Agents operate in isolated worktrees, and humans manually review code and trigger merges into `main`. It guarantees no automated background merges that might corrupt the primary working directory.
* **Vibe Mode:** A specialized zero-knowledge workspace. The backend `VibeOrchestrator` automatically captures stack traces and handles Git conflict resolution, completely abstracting away the CLI and version control from the user interface.

## Documentation

For an in-depth understanding of GitLoom's architecture, swarm mechanics, and implementation details, refer to:
* [Technical Roadmap & Architecture Blueprint](GitLoom_Roadmap.md)
* [Implementation Plan](Implementation_Plan.md)
