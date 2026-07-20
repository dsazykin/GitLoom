# GitLoom

**Safe, autonomous multi-agent coding — on your own machine.**

GitLoom is a native, high-performance Git GUI that doubles as a **control center for a swarm of AI coding agents**. Run several autonomous agents at once, each jailed in its own hardened sandbox on its own branch, and merge their work only after it has been **verified against your current `main` and reviewed by you**. You stop being the person typing every line and become the engineering manager approving what ships.

Built on **.NET 10 · Avalonia 11 · LibGit2Sharp · SQLite/EF Core**.

---

## The problem

Modern agentic CLIs (Claude Code, Codex, Jules, OpenCode, …) are excellent at building whole features — but running *several* of them autonomously is a mess:

- They **step on each other** and on your uncommitted work, and collide on `.git/index.lock`.
- They edit your working directory live, so one bad run **breaks your environment**.
- You **can't trust their output** enough to merge it blindly — and reviewing N branches by hand across N terminals doesn't scale.

Managing a swarm in split terminals turns into babysitting. GitLoom exists to make it *safe and orchestrated* instead.

---

## The core idea: a trustless, verified merge queue

GitLoom's thesis — and its moat — is **"safe-to-merge."** Agents don't push to `main`; they land in a merge queue that guarantees what merges is actually sound:

- **It runs your own verification** (build + tests) inside the agent's isolated container, and the verdict is the **container's real exit code** — read by the trusted daemon from the container runtime, *outside* the container. An agent **cannot forge a "passed"** by printing success.
- **It's always verified against *current* `main`.** When any branch merges, every other verified branch is invalidated and **re-verified against the new `main`** before it's eligible — no "green when I opened it, but main moved 20 commits" gap.
- **It's provenance-pinned:** the resolved test command + config are recorded, so an agent can't sneak through by weakening what "verify" means.
- **It never auto-merges.** Verification makes a branch *eligible*; **you** approve it in a risk-ranked review cockpit, and the merge itself is an atomic compare-and-swap so racing agents can't slip a stale merge in.

The guarantees are the ones you specifically need *because an AI wrote the code and several agents are racing to merge at once.*

---

## What's built

GitLoom started as a polished Git client and has grown the agent platform underneath it. Status is marked honestly.

### The Git client — **shipped & stable**
A blazing-fast, natively rendered client that stands on its own:
- **Commit history & graph** — an isolated DAG lane-routing engine on a virtualized vector canvas, 60 FPS on complex histories.
- **Staging, diffs & committing** — side-by-side and unified diffs, **hunk- and line-level partial staging** on a pure patch engine validated against `git apply`, push/pull with ahead/behind tracking.
- **Conflict resolution** — a synchronized 3-pane merge editor (Ours | Result | Theirs) that merge/rebase/cherry-pick/pull all route into.
- **Branches, tags & worktrees** — checkout-safety validation, full tag lifecycle, git-worktree porcelain.
- **Five switchable themes** — a tokenized design system: Midnight Loom (default), Daylight Loom (light), Command Deck, Atelier, Loom Aurora.

### The agent platform — **engines built & tested**
Each of these is implemented behind a clean interface and covered by tests (including real-Docker tests in CI):
- **Hardened agent sandboxes** — every agent runs in a locked-down container (no-new-privileges, seccomp, dropped caps, read-only rootfs, user-namespaced) with a **default-deny egress proxy**: model APIs and package registries are reachable; **the git host is not** — so an agent can't clone/exfiltrate. Toolchains are pre-baked, so nothing fetches at runtime.
- **The verified merge queue** — the safe-merge engine described above (stale invalidation, daemon-observed verification, exactly-once atomic merge).
- **Risk-ranked review cockpit** — per-hunk provenance, a flagged-changes acknowledgement gate, branch-vs-`main` diffs.
- **Coordinator + plan approval** — a coordinator agent decomposes work into a structured **plan you approve before any worker spawns**; the approver identity is derived by the daemon (not client-supplied), and managed workers' terminals are locked at the gRPC layer.
- **Always-visible kill switch** — freezes the merge queue *first*, then pauses every agent, with a hard timeout ceiling that a compromised worker can't stretch.
- **AI gateway** — BYOK keys via the OS keyring; per-agent and per-day token/cost budgets, rate-limit backoff, admission control.
- **External PR intake** — subscribe bot-authored PRs (Codex/Jules/Copilot) into the same verify→review→merge pipeline.
- **Native terminals** — real OS pseudo-terminals (ConPTY/forkpty) rendered with Skia, so interactive CLIs and fast logs work without dropped keystrokes.
- **GitLoomOS bootstrapper** — a lightweight background Linux VM (WSL2) gives agents native ext4 Docker performance while you keep a native Windows UI (no `/mnt/c` 9P latency, no Docker Desktop dependency).

### In final assembly — **the Alpha integration**
The pieces above are being wired into a single runnable control center — launch → spawn a real sandboxed agent → drive it → verify → review → merge. The real container spawn is validated in CI; the GUI surfaces are in live testing now.

### Planned — **the roadmap beyond Alpha**
A turnkey installer/OOBE, a tamper-evident audit trail + SIEM streaming, an optional AI-reviewer pass, cross-worktree conflict radar, a production terminal engine, and **"Vibe Mode"** (a zero-terminal experience that auto-heals dev-server errors for non-developers). These are specified, not yet built.

---

## Architecture

A native Avalonia UI talks over gRPC to a **headless daemon** that owns everything privileged — sandboxes, the merge queue, verification, budgets, and audit. The UI never touches Docker directly. Agents live in per-repo persistent jails inside the GitLoomOS VM; their worktrees are ext4-native, and the daemon is the only component permitted to reach a git host (via a read-only proxy). One design system drives five live-switchable color themes across the whole surface.

**Under the hood:** Avalonia 11 · `CommunityToolkit.Mvvm` · `LibGit2Sharp` · SQLite/EF Core · gRPC · Docker.

---

## Status

| Layer | State |
|---|---|
| Git client | **Stable** — usable today |
| Agent platform engines (sandbox, merge queue, cockpit, coordinator, gateway, terminal, bootstrapper) | **Built & tested** |
| End-to-end assembly (runnable swarm) | **In final integration** |
| Turnkey installer, audit/SIEM, AI review, Vibe Mode | **Planned** |

GitLoom is in active development — the foundation is real and tested; the fully packaged, one-click product is on the way.

---

## Getting started (developer preview)

Requires the **.NET 10 SDK** (pinned via `global.json`, so `dotnet` picks the right toolchain automatically).

```bash
git clone <this repo>
cd GitLoom
dotnet restore
dotnet build                      # build the whole solution
dotnet run --project GitLoom.App  # launch the GUI
```

Or open `Mainguard.slnx` in Visual Studio / Rider.

### Containerized build & test (optional)

A Docker image reproduces the exact .NET 10 build/test toolchain (plus native `LibGit2Sharp`/`SkiaSharp` deps) so builds and tests run identically anywhere — **for building, testing, and EF migrations only, not the GUI**:

```bash
docker compose run --rm build     # restore + build the solution
docker compose run --rm test      # run all test suites headlessly
docker compose run --rm shell     # interactive toolchain shell (e.g. dotnet ef ...)
```

---

## Documentation

- [`AGENTS.md`](AGENTS.md) — architecture, the design system, conventions, and the current repository map (kept in sync with the code).
- [`docs/security-architecture.md`](docs/security-architecture.md) — the sandbox, egress, and merge-safety security model.
- [`docs/phase-2/`](docs/phase-2/) — the multi-agent platform design and implementation plans.
