# GitLoom

**A Multi-Agent Control Center & Premium Git GUI**

![GitLoom Screenshot]()

## The Vision: Control Center for AI Agents
As AI coding tools (Claude Code, AGY, OpenCode) get more autonomous, developers are ending up with multiple terminal windows running chaotic, parallel tasks that step on each other's toes or overwrite human work. 

GitLoom isn't just a Git GUI; it's a command-and-control dashboard. It elevates you from writing code to an "Engineering Manager," orchestrating a swarm of AI workers from a single, beautiful interface.

## Key Selling Points

### 1. Seamless Synchronous Collaboration
**Code side-by-side with your swarm in perfect harmony.** 
GitLoom’s "Middle Manager Architecture" perfectly synchronizes state between the human and the agents. You can be actively coding a feature in your IDE on the `main` branch, while Agent A builds a database schema and Agent B designs a frontend component. GitLoom's background daemon handles "Keep-Alive" rebases to ensure agents safely inherit your latest saves without lock collisions, and cross-agent dependencies are orchestrated gracefully. It feels like working synchronously with a real dev team in the same room.

### 2. True, Conflict-Free Concurrency
**Never let an AI break your working directory again.**
Most AI tools edit files live in your IDE. GitLoom jails every agent in its own isolated Linux container and Git worktree. They can write code, run dev servers, and make mistakes completely independently. You review their branches securely in the foreground and click "Merge" only when you're happy.

### 3. Flawless Environment Setup
**Native performance on Windows. Native environments on Linux.**
GitLoom bypasses Docker Desktop and 9P volume latency. It silently installs a lightweight `GitLoomOS` (Linux) in the background. Agents get perfect Node/Python environments with zero setup natively on Linux, while you enjoy a blazing fast, native Windows Avalonia UI.

### 4. JetBrains-Grade Native Terminals
**Say goodbye to laggy browser terminals.**
Many modern dev tools rely on sluggish web-based terminals (`xterm.js`) embedded in Electron apps. GitLoom uses real OS-level pseudo-terminals (`Pty.Net`) rendered with Skia. Interactive CLIs, curses interfaces, and fast-scrolling logs work flawlessly without dropping keystrokes.

### 5. "Vibe Mode"
**Code complex apps without ever seeing a terminal.**
Vibe Mode isn't just a simplified UI—it actually introduces a backend "Virtual Developer." This autonomous orchestrator intercepts stack traces, feeds them back to the AI for auto-healing, and handles Git conflicts automatically. Perfect for designers and founders who want results without managing the CLI.

## Under the Hood
- **Desktop Framework:** Avalonia UI (v11.1.3 - Stable)
- **MVVM Engine:** `CommunityToolkit.Mvvm` (v8.4.2)
- **Git Engine:** `LibGit2Sharp` (v0.30.0+)
- **Local Database:** SQLite via Entity Framework Core (`Microsoft.EntityFrameworkCore.Sqlite`)
- **Terminal Backend:** `Pty.Net`
- **Windowing Layout:** `Dock.Avalonia`

## Documentation
For an in-depth understanding of GitLoom's architecture, swarm mechanics, and implementation details, refer to:
* [Technical Roadmap & Architecture Blueprint](GitLoom_Roadmap.md)
* [Implementation Plan](Implementation_Plan.md)
