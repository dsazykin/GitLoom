# GitLoom — Master Implementation Document v2 (Agent Platform)

**Date:** 2026-07-07
**Branch:** this document lives on — and all its tasks are built on — the **`phase2`** branch (see §0.0).
**Supersedes for execution purposes:** §5 ("Later phases") of `GitLoom_Master_Implementation_Document.md` and the strategy-level specifications of workstreams F6, G, H, I, J, K in `GitLoom_Implementation_Strategy.md` (which remains the strategic index).
**Market inputs:** `docs/market-analysis/GitLoom_Market_Research_v2.md`, `GitLoom_Viability_And_Differentiation_2026-07.md`, `GitLoom_Naming_And_Competitive_Landscape_2026-07.md`, and the per-competitor refresh `GitLoom_Competitor_Research_2026-07-07.md` (every company from the landscape doc re-verified 2026-07-07). §1.2 records exactly how those findings changed the plan.

---

## 0. How to use this document

### 0.0 Branch policy (binding)

The core Git client (everything specified in Master Doc v1, tasks T-01…T-33) lives on **`main`** and is in release-hardening mode: **bug fixes, UI polish, and test work for v1 features target `main`**, one issue per PR, exactly as before.

Everything in *this* document — the agent platform (Phases 6–9), the installer, and the market-driven additions — is built on the **`phase2`** branch:

1. `phase2` tracks `main`: merge `main` into `phase2` routinely (at minimum after every v1 fix batch). Conflicts are resolved on `phase2`, never by rebasing published history.
2. Task branches fork from `phase2` and their PRs target `phase2` (`gh pr create --base phase2`).
3. When the core client is declared released, `phase2` merges into `main` once, and `main` becomes the single center again. This document then applies to `main` and this section collapses to "one task = one PR".
4. Nothing in this document may be merged to `main` before that point. A v1 fix discovered while working here is cherry-picked out to a `main`-targeted PR.

### 0.1 If you are IMPLEMENTING a task

Identical protocol to Master Doc v1 §0.1, restated:

1. Find your task in §4 (`P2-xx`, ordered in §3). Do not start a task whose *Depends on* list is not fully merged to `phase2`.
2. The **Contract** is binding (namespaces, signatures, parameter names/order). **Implementation steps** are a known-good path; deviations must still satisfy every **Invariant**.
3. Build after each step; never reorder steps touching the same file.
4. Every **Edge-case matrix** row is part of the definition of done and has a required test.
5. The **Required tests** block in each task is the test contract (v2 embeds it inline instead of a separate companion doc). Tests land **in the same PR**.
6. Run the task's **Reviewer verification script** yourself before opening the PR.
7. Never bundle two task IDs in one PR.

### 0.2 If you are REVIEWING a PR for a task

Same priority order as v1 §0.2: Contract → Invariants → Rejection triggers → Acceptable variations (never request changes for these) → Required tests green in CI → run the verification script (< 5 min).

### 0.3 Global PR rules

1. One task = one PR; foundation work never bundled with feature work.
2. PR description links the task ID, lists manual verification performed (output/screenshots), names the tests added.
3. Any PR touching `GitLoom.Core/Services/GitServices.cs` runs the full suite locally before pushing; any PR touching terminal code runs the P2-04 harness.
4. Security-relevant PRs (P2-01, P2-07, P2-08, P2-11, P2-15, P2-22) execute their listed security checks and paste the evidence into the PR; the reviewer re-runs at least one.
5. No PR may reintroduce: `cmd.exe` shells, secrets in argv/URLs/logs/exception text, `BuildSignature` call sites outside `GetSignature`, blocking Git/network work on the UI thread, `Directory.Delete` in discard paths, untyped throws, **Windows bind mounts into containers**, **`wsl --shutdown`**, or bare `git push --force`.

---

## 1. Baseline — what exists on `main` (2026-07-07)

### 1.1 The shipped Git client

Master Doc v1 is **fully implemented and merged**: audit fixes 1.1–1.13, tasks T-01…T-22, and the host-integration extension tasks T-23…T-33 (PR/issue/review/checks/notifications/releases panels, PR-to-worktree checkout, pre-commit safety scanner, conventional-commit composer, blame→PR jump). The suite stands at **1,042 tests, 0 skipped, deterministic** (parallelization disabled by design). Key structural facts an implementer must not fight (unchanged from v1 §1):

- **No DI container.** Services are instantiated directly; `App` exposes a static `Settings`.
- All LibGit2Sharp access goes through `IGitService.ExecuteWithRepo(...)`; never hold a `Repository` long-lived.
- Policy split (G-7 of v1): LibGit2Sharp for reads/status/commit/diff; git CLI for interactive rebase, worktrees, partial staging, force-with-lease, LFS, stash pop/apply — via the hardened `RunGit` family only.
- Typed exception hierarchy `GitLoomException` → (`MergeConflictException`, `GitIdentityMissingException`, `AuthenticationRequiredException`, `RemoteNotFoundException`, `GitOperationException`, …).
- Host API access goes through **one audited transport per host** (`GitHubApiClient`) — token in the `Authorization` header only; a second copy is a rejection trigger.
- Secrets: `SecureKeyring` (DataProtection, DPAPI-wrapped key ring on Windows), keys `token_<host>` / `sshpass_<keypath>`; `CredentialResolver` is the single SSH-vs-token decision point.
- `GitLoom.slnx`, .NET 10 (`global.json` pinned 10.0.100), Avalonia 11 + CommunityToolkit.Mvvm, xUnit in `GitLoom.Tests` (references Core *and* App — headless Avalonia harness TI-00 exists).
- Five switchable color themes; design tokens only (`{DynamicResource}`), component classes over raw colors; **Repository Map in `AGENTS.md` must stay current with every file add/move/delete.**

### 1.2 What the July 2026 market findings changed (traceability)

| Finding (doc) | Plan change in v2 |
|---|---|
| "Spawn agents in worktrees" is commoditized — native in Claude Code, GitKraken Agent Mode, GitHub Copilot app (Naming §2, Viability §1.1–1.2) | Orchestration UX is built to parity, not led with; the **merge/verification pipeline is promoted to the product spine** (P2-10 before any coordinator polish) |
| Verification is the bottleneck: PR review time +91%, 46% distrust agent output (Viability §1.3) | **P2-10 merge queue + verification runs** and **P2-11 risk-ranked review cockpit with per-hunk provenance** are new top-priority product tasks, not infrastructure |
| EU AI Act enforcement window opens 2026-08-02 (Viability §1.4) | **P2-15 hash-chained audit** and **P2-16 SIEM export** pulled forward from "post-GA enterprise" into the same milestone as the coordinator |
| Vendor lock-in risk + Anthropic subscription-OAuth ban (Market v2 §5.4) | **P2-12 external agent PR intake** (Codex/Jules/Copilot PRs through the same verify→review→merge pipeline) added — it was in no earlier plan; agent-agnostic pinned **adapter channel** (P2-22) is a survival requirement; API-key BYOK is the primary documented path with ToS notice (P2-01) |
| Rate limits break the first-run experience (Market v2 §5.4 #2) | **P2-08 AI Gateway is launch-blocking**, not an enterprise add-on |
| Windows/WSL2 flank is unserved; Conductor is Mac-only (Viability §1.2, D-4) | Hardened WSL2 sandbox + published security architecture (P2-07, P2-17) kept P0 and marketed |
| GitKraken predictive conflict detection is single-user (Viability D-5) | **P2-19 cross-worktree conflict radar** (N live agent worktrees vs each other and main) added as a new differentiator |
| Agent WIP commits are unreviewable noise (Viability D-5) | **P2-20 agent commit-stream curation** (one-click squash checkpoints → conventional commits, built on T-08 rebase) added |
| Hardware caps local swarms at 4–6 agents (Market v2 §6) | Honest admission control (P2-08); **cloud worktrees promoted from "pivot" to roadmap** (P2-25 guardrails now, beta ≤ 2 quarters post-GA) |
| Vibe Mode local install can't win (Market v2 §5.1) | K-stream stays post-v1 and cloud-first; only the shared `VibeOrchestrator` engine (P2-26) ships with the desktop platform |
| AI commit messages / chat-with-repo are GitKraken-owned checkboxes (Viability §3) | **Deliberately not built.** Out of scope in v2 exactly as in v1 |
| Client-parity gaps vs GitKraken/Fork power users (Backlog A-1…A-3) | Client-parity track **P2-C1 bisect / P2-C2 global search / P2-C3 multi-repo dashboard** — the dashboard doubles as the swarm control surface |
| **Agent Trace** attribution standard emerged (Cognition/Cursor RFC, backed by Cloudflare/Vercel/Google/Amp; no product renders it yet) (Competitor refresh §b) | P2-11 provenance is built on **Agent Trace as the interchange format** — GitLoom emits it from the orchestrator and is the **first review UI to render it**; commit trailers remain the fallback for non-emitting agents |
| Docker Sandboxes (sbx) went GA (2026-01-30) with microVM isolation + default-deny egress presets **that work on Windows** (Competitor refresh) | P2-07 keeps the native WSL2 sandbox as the zero-extra-install default and adds **sbx as an optional "maximum isolation" backend** — the moat is the integration (worktree+queue+audit+UI), not the hypervisor |
| Jules has a public API + GitHub Action; Codex/Devin PR producers multiply; Kepler ships PR-based task intake (Competitor refresh §e) | **P2-12 external PR intake accelerated into M7 core** (it is cheap to build now and the field is empty at the vendor-neutral level) |
| GitHub's server-side merge queue is the only stale-invalidation analogue — server-side, CI-bound, PR-only, not agent-aware (Competitor refresh §a) | P2-10 positioning sharpened: works **pre-PR, locally, across N agent branches, without CI round-trips, and for repos not on GitHub** |
| EU AI Act Art. 12 requires logging/traceability but not literally cryptography (Competitor refresh §c) | P2-15 split: ship the **evidence pack** (hash chain + identity + `audit verify` + SIEM feed) for the 2026-08-02 marketing moment; RFC 3161 anchoring may trail; claims say "audit-grade/tamper-evident", never "legally required crypto" |
| Sculptor's **Pairing Mode** (one-click sync of a sandboxed agent's work into the local repo) is the best-in-class hand-back UX (Competitor refresh §10) | P2-11 cockpit includes a pairing-style **"bring this branch local"** action (fetch agent branch → local worktree/checkout via existing T-29 plumbing) |
| **MergeLoom (mergeloom.ai)** — governance-positioned "-Loom" competitor found 2026-07-07 (Competitor refresh) | Not an engineering task: **naming risk escalated** — flagged to the owner; the naming decision doc should treat this as a forcing function |

---

## 2. Global engineering invariants (every PR, every task)

G-1 … G-10 from Master Doc v1 §2 apply unchanged. v2 adds:

| # | Invariant | Reviewer check |
|---|---|---|
| G-11 | **No Windows-path bind mounts into containers, ever.** The only cross-boundary repo data path is Git objects (fetch/push between the Windows repo and the ext4 bare repo) | `docker inspect` on any agent container shows zero `/mnt/c`, `drvfs`, or UNC mounts (P2-06/P2-07 tests assert this) |
| G-12 | **Never `wsl --shutdown`** (kills the user's personal distros); lifecycle is `--terminate GitLoomEnv` → poll → `--unregister` | `grep -rn "wsl --shutdown\|--shutdown" GitLoom.Core/ GitLoom.Server/ installer/` → 0 hits |
| G-13 | Secrets (API keys, tokens, passphrases) cross process boundaries only via: OS keyring, tmpfs files (mode 0400), or authenticated gRPC message fields explicitly marked `// SECRET` and excluded from logging interceptors. Never argv, never env files on persistent disk, never proto logs | grep new `ProcessStartInfo`/proto/log sites; the gRPC logging interceptor has a field-mask test |
| G-14 | Every proto change is **transport-agnostic**: no localhost assumptions, no daemon filesystem paths leaking to the client except opaque handles | review `GitLoom.Protos` diffs; WAN-latency CI job (P2-25) stays green |
| G-15 | Agent containers: `no-new-privileges`, userns remap, memory+pids limits, default-deny egress. A container spawned without the hardened spec is a bug, not a variation | P2-07 verification script |
| G-16 | **No `docker build` at runtime** (severs PTYs); toolchains sideload via `devbox add` into the static base image | grep daemon code for `ImageBuild` |
| G-17 | Every agent-initiated ref mutation, spawn/kill, plan approval, and merge decision emits an audit event (hash-chained once P2-15 lands; plain journal rows before that) | new mutation RPCs show an `AuditLog.Append` call in the same change |
| G-18 | The UI never talks to Docker/WSL/PTYs directly — only through the daemon's gRPC surface. The daemon never renders UI strings (typed error codes + params; the client localizes) | review: no `Docker.DotNet`/`Porta.Pty` references in `GitLoom.App` |

---

## 3. Build order and dependency graph

Two parallel tracks. The **platform track** is strictly ordered; the **client-parity track** (P2-C1…C3) has no platform dependencies and can interleave anywhere (they also make good "first task on phase2" warm-ups).

```
PLATFORM TRACK
P2-01 BYOK key store + health check      (no deps)
P2-02 Daemon + gRPC v1 contract          (no deps)
P2-03 Terminal engine, interim PTY       (P2-02)
P2-04 VT conformance & replay harness    (P2-03; gates P2-03 and P2-18)
P2-05 GitLoomOS bootstrapper             (P2-02)
P2-06 Repo provisioner (git-native sync) (P2-02, P2-05)
P2-07 Sandbox hardening + egress         (P2-05, P2-06)
P2-08 AI Gateway + admission + reconcile (P2-01, P2-07)   ← launch-blocking
P2-09 Agent lifecycle + yield + rebase   (P2-06, P2-07)
P2-10 Merge queue + verification runs    (P2-09)          ← product spine
P2-11 Review cockpit: risk rank + provenance + flagged gate (P2-10)
P2-12 External agent PR intake           (P2-10; reuses T-23/T-29)
P2-13 Activity bar & docking UI          (P2-02, P2-03)
P2-14 Plan approval + dual-mode orchestration (P2-08, P2-09, P2-13)
P2-15 Hash-chained audit log             (P2-14 approval records; start after P2-10)
P2-16 SIEM exporter                      (P2-15)
P2-17 Source-available split + network transparency (P2-07 proxy logs)
P2-18 Terminal target engine (libvterm)  (P2-04 green; before beta)
P2-19 Cross-worktree conflict radar      (P2-06)
P2-20 Agent commit-stream curation       (P2-09; reuses T-08)
P2-21 Installer: diagnostics → OOBE → payload (P2-05)
P2-22 Windows integration + adapter channel + teardown (P2-21)
P2-23 RBAC / SSO / SCIM                  (P2-15, P2-16)
P2-24 Supply-chain & secrets compliance  (P2-10 gate UI, P2-01 keystore)
P2-25 Cloud worktrees (guardrails now; implementation post-GA) (P2-02…)
P2-26 VibeOrchestrator engine (shared)   (P2-03, P2-08)

CLIENT-PARITY TRACK (any time; PRs may also target main*)
P2-C1 Interactive bisect assistant       (T-19 journal — already on main)
P2-C2 Global fuzzy search                (T-18 matcher, T-23/T-24 sources)
P2-C3 Multi-repo dashboard               (T-10 auto-fetch)
```

\* Client-parity tasks touch only v1 systems. If shipped before the phase2 merge they may target `main` as ordinary client features — decide per task with the repo owner; default is `phase2`.

**Milestones:** M6 = P2-01…P2-08 (one hardened agent, gateway-protected). M7 = P2-09…P2-14 + P2-18, P2-21, P2-22 (the verified swarm + installer). M7.5 = P2-15…P2-17, P2-19, P2-20 (trust: audit + radar + curation — target **before 2026-08-02** for the audit pair). M8 = P2-23…P2-26 (enterprise + cloud + vibe engine). Client-parity anywhere.

---

# 4. TASK SPECIFICATIONS

---

## P2-01 — BYOK key store + key health check (F6)

**Milestone:** M6 · **Priority:** P0 (agents need keys before anything else works) · **Depends on:** nothing.

### Why

Phase-7 agents consume LLM API keys. Keys must live in the OS keyring (never plaintext config), be validated at entry so the user learns their realistic concurrency ceiling *before* the first 429, and be injectable into sandboxes via tmpfs only. The Anthropic subscription-OAuth ban (enforced 2026-04-04) makes the API-key path the primary documented one, with a recorded ToS acknowledgment for CLI-OAuth users.

### Contract (must exist exactly)

```csharp
// GitLoom.Core/Security/ISecureKeyStore.cs
namespace GitLoom.Core.Security;

public interface ISecureKeyStore
{
    void Set(string key, string secret);
    string? Get(string key);
    void Delete(string key);
}
// SecureKeyring implements ISecureKeyStore (Set/Get/Delete delegate to Save/Retrieve/DeleteSecret).
// Key names for LLM keys: "llm_anthropic", "llm_openai", "llm_<provider>" (filesystem-safe, mirrors token_<host>).
```

```csharp
// GitLoom.Core/Security/ApiKeyHealthService.cs
public sealed class KeyHealth
{
    public bool IsValid { get; init; }
    public string? FailureReason { get; init; }          // token-scrubbed
    public int? RequestsPerMinute { get; init; }         // from provider rate-limit headers
    public int? TokensPerMinute { get; init; }
    public int EstimatedConcurrentAgents { get; init; }  // conservative mapping table in code
}
public sealed class ApiKeyHealthService
{
    public ApiKeyHealthService(HttpMessageHandler? handler = null);   // seam for offline tests
    public Task<KeyHealth> CheckAsync(string provider, string apiKey, CancellationToken ct);
}
```

```csharp
// GitLoom.Core/Security/CredentialInjector.cs  (contract now; daemon side consumes it in P2-07)
public static class CredentialInjector
{
    /// <summary>Env-file content for an agent (KEY=value lines), built in memory only.</summary>
    public static string BuildEnvFileContent(IReadOnlyDictionary<string, string> secrets);
}
```

Plus `ApiKeySettingsViewModel` + settings page (masked entry, provider dropdown, per-provider Save/Delete, health result line) and the CLI-OAuth ToS notice dialog whose acknowledgment (provider, timestamp) persists via `AppDbContext` (new table + migration).

### Implementation steps

1. Extract `ISecureKeyStore`; `SecureKeyring : ISecureKeyring, ISecureKeyStore`. No behavior change; the interface is what the daemon and P2-24 backends implement later.
2. `ApiKeyHealthService.CheckAsync`: Anthropic → `POST /v1/messages` with `max_tokens: 1` and the key in the `x-api-key` header; OpenAI → `GET /v1/models`, `Authorization: Bearer`. Parse rate-limit headers (`anthropic-ratelimit-requests-limit`, `-tokens-limit`; OpenAI `x-ratelimit-*`). Map to `EstimatedConcurrentAgents` via a static table (document the table in code; be conservative). 401/403 → `IsValid=false` with the provider's message **scrubbed of the key** (reuse the `GitHubApiClient.Redact` pattern — do not duplicate it: move `Redact` to a shared internal `Http/RedactionExtensions` if needed).
3. Settings page: masked `TextBox` (`PasswordChar`), validate-on-save (invalid key is **not stored**), success renders "Key valid — supports ~N concurrent agents". Null out local copies after storing.
4. ToS notice: shown when the user selects "use my Claude subscription (CLI OAuth)" — text states the April-2026 restriction and that API-key is the supported path; acknowledgment recorded before the option activates.
5. `CredentialInjector.BuildEnvFileContent`: pure string building (`ANTHROPIC_API_KEY=...` etc.), newline-terminated, no quoting games (values are opaque tokens; reject values containing `\n` with a typed throw).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| invalid key | inline error, key absent from keyring and from any log/exception |
| valid key, headers missing | `IsValid=true`, ceilings null, agents estimate = 1 (conservative floor) |
| provider unreachable | typed failure, retry affordance, nothing stored |
| key value containing newline | typed `ArgumentException` from the injector (env-file integrity) |
| re-save over an existing key | old value overwritten atomically, health re-checked |
| delete | keyring entry gone (verify file removed) |

### Invariants (MUST)

1. The key appears only in: the keyring, the in-memory HTTP header of the health check, and (later) tmpfs env content. Never argv, settings JSON, logs, or exception text.
2. Health check is fully offline-testable through the `HttpMessageHandler` seam (recorded fixtures).
3. An invalid key is never persisted.
4. ToS acknowledgment persists across restarts and is queryable (P2-15 will chain it).

### Rejection triggers

- A second copy of token-scrub logic (reuse/move the existing one).
- Health check called on the UI thread or without cancellation.
- Any `llm_*` value readable from `UserPreferences`/`config.json`.

### Reviewer verification script

```bash
dotnet test --filter "FullyQualifiedName~ApiKeyHealth|FullyQualifiedName~CredentialInjector|FullyQualifiedName~SecureKeyStore"
grep -rn "llm_" GitLoom.App/ | grep -i "preferences\|settings.json"   # 0 hits
```

**Required tests:** health-check parser fixtures (valid + 401 + missing headers, per provider); ceiling table; injector purity + newline rejection; keystore round-trip through the new interface; VM test: invalid key → not stored (keyring dir empty).

---

## P2-02 — `GitLoom.Server` daemon + gRPC v1 contract (G-7.0)

**Milestone:** M6 · **Priority:** P0 · **Depends on:** nothing.

### Why

Every Phase-7 feature needs a process to live in: a headless daemon that owns containers, PTYs, VM worktrees, the merge queue, the gateway, and (in the VM) its own SQLite. The UI becomes a gRPC client for agent features; existing local-repo Git features stay in-process.

### Contract

New projects `GitLoom.Server` (ASP.NET Core gRPC host, linux-x64 publish) and `GitLoom.Protos` (proto-first, `Grpc.Tools` codegen, consumed by Server and App). Package `gitloom.v1`, services:

- `AgentService`: `SpawnAgent`, `StopAgent`, `ListAgents`, `StreamAgentEvents` (server-stream).
- `TerminalService`: `Attach(agentId)` bidi stream; the output frame is `oneof { bytes raw; GridUpdate grid; }` **from day one** (P2-18 must not be a proto break).
- `RepoSyncService`: `ProvisionRepo`, `CreateWorktree`, `ListWorktrees`, `RemoveWorktree` (bodies land in P2-06; the RPCs and typed `UNIMPLEMENTED` stubs land here).
- `GatewayService`: `GetBudgets`, `SetBudgets`, `StreamSpend` (bodies in P2-08).

Client side: `GitLoom.App/Services/DaemonClient.cs` — channel creation, token metadata, reconnect-with-backoff, `IObservable`-style connection state the Activity Bar renders.

### Implementation steps

1. Add the two projects to `GitLoom.slnx`; protos compile into both; `dotnet build` stays green from the first commit.
2. **Auth:** on startup the daemon writes a random 256-bit session token to a file readable only by the user, prints nothing; an interceptor requires it as `authorization: bearer <token>` metadata on every call; everything else → `PERMISSION_DENIED`. Bind `127.0.0.1` only.
3. **`--local-dev` flag:** daemon runs directly on Windows/localhost (no WSL) for the dev loop and CI.
4. **Logging interceptor** with a secret field-mask (G-13): proto fields commented `// SECRET` are registered in a mask table and never logged.
5. `DaemonClient` with reconnect/backoff + connection-state stream; a `Connected/Degraded/Down` enum consumed by the UI later (P2-13).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| missing/wrong token | `PERMISSION_DENIED`, connection not degraded into a retry storm |
| daemon restart mid-stream | client reconnects with backoff; `StreamAgentEvents` resumes |
| port already bound | typed startup failure naming the port |
| token file deleted while running | existing channels keep working; new client launch regenerates on daemon restart |

### Invariants (MUST)

1. Every RPC authenticated by the interceptor — no allowlist of "public" methods.
2. Daemon binds loopback only (assert in an integration test on the listening endpoint).
3. Proto files carry no OS paths in client-facing messages except opaque handles (G-14).
4. The daemon builds and runs on both linux-x64 and Windows (`--local-dev`); CI exercises the latter.

### Rejection triggers

- Business logic in gRPC service classes beyond validation/dispatch (logic goes in `GitLoom.Core`/daemon services so it is unit-testable).
- Client code referencing server-only assemblies.
- Any RPC without a deadline/cancellation path.

**Required tests:** in-proc daemon (`WebApplicationFactory`) — authenticated call OK, wrong token `PERMISSION_DENIED`; terminal bidi echo; reconnect resumes event stream; logging mask test (a `// SECRET` field never appears in captured logs).

---

## P2-03 — Terminal engine, interim: PTY shim + vendored renderer (G-7.1a)

**Milestone:** M6 · **Priority:** P0 · **Depends on:** P2-02. **Gated by:** P2-04 from day one.

### Contract

```csharp
// GitLoom.Core/Agents/PtyProcessShim.cs
public sealed class PtySession : IDisposable
{
    public Stream IO { get; }
    public void Resize(int cols, int rows);
    public void Kill();
    public Task<int> ExitCode { get; }
}
public static class PtyProcessShim
{
    public static PtySession Spawn(string command, IReadOnlyList<string> args, string cwd,
        IReadOnlyDictionary<string, string> env, int cols, int rows);
}

// GitLoom.Core/Terminal/ITerminalView.cs
public interface ITerminalView
{
    void FeedOutput(ReadOnlyMemory<byte> data);
    event Action<byte[]>? InputAvailable;
    void Resize(int cols, int rows);
    object GetStateSnapshot();
    void RestoreState(object snapshot);
}

// GitLoom.Core/Terminal/VtBoundaryDetector.cs (pure)
public sealed class VtBoundaryDetector
{
    /// <summary>Returns the largest prefix length of <paramref name="buffer"/> that ends on a
    /// VT-sequence and UTF-8 codepoint boundary; bytes beyond it are held for the next frame.</summary>
    public int SafeFlushLength(ReadOnlySpan<byte> buffer);
}
```

Daemon side: `TerminalStreamer` — PTY bytes pooled, flushed every 16 ms as one gRPC `raw` frame, never splitting a VT sequence or UTF-8 codepoint (holdback cap 4 KB, then flush regardless).

### Implementation steps

1. PTY shim over `Porta.Pty` — ConPTY on Windows (dev loop), forkpty on Linux (daemon). `cwd` locked to the agent worktree.
2. `VtBoundaryDetector`: state machine Ground/Esc/CSI/OSC/DCS/SS3 + UTF-8 continuation counting.
3. `TerminalStreamer`: `ArrayPool<byte>` buffers, 16 ms ticker, detector-guarded flush.
4. Vendor `Iciclecreek.Avalonia.Terminal` into `external/` (license retained), adapt behind `ITerminalView`.
5. `TerminalViewModel` + `TerminalView`: keystrokes (incl. 0x03) → input stream; resize propagates; 60 FPS dirty-flag invalidation; 10k-line circular scrollback.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| frame boundary lands mid-CSI / mid-OSC / mid-emoji | detector holds the tail; reassembly byte-identical |
| malformed endless escape | 4 KB holdback cap flushes anyway |
| `yes | head -c 100M` | memory flat (pooled buffers + scrollback cap) |
| Ctrl+C | 0x03 reaches the PTY; foreground process interrupts |
| resize while streaming | no torn frames, TUI reflows |

### Invariants (MUST)

1. `isatty()` is true inside the PTY (probe test).
2. Detector is pure and exhaustively tested (every fixture sequence split at **every** byte offset).
3. No terminal logic in code-behind; the renderer sits behind `ITerminalView` so P2-18 swaps engines without ViewModel changes.

### Rejection triggers

- Raw `Process` with redirected pipes standing in for a PTY.
- Renderer APIs leaking into ViewModels (breaks the P2-18 swap).

**Required tests:** detector split-at-every-offset corpus (CSI SGR, OSC 8 both terminators, DCS, 2/3/4-byte UTF-8, ZWJ emoji); `/bin/cat` echo round-trip; curses probe; scrollback cap.

---

## P2-04 — VT conformance & replay harness (G-7.1c)

**Milestone:** M6 · **Priority:** P0 — starts alongside P2-03 and gates it and P2-18. · **Depends on:** P2-03 interfaces.

Contract summary (v1-strategy §G-7.1c verbatim, binding): `vttest`/`esctest` scripted headless with a checked-in known-failures allowlist (progress monotonic); golden transcripts under `GitLoom.Tests/Transcripts/` (Claude Code, OpenCode, vim, htop 60 s, tmux) replayed byte-order-only and compared cell-by-cell against committed goldens; required coverage matrix — alternate screen, DEC 2026 synchronized output, truecolor, CJK/emoji width, bracketed paste, mouse reporting, OSC 8 hyperlinks; harness drives **both** engines through a "feed bytes → read grid" abstraction (P2-03's control gains a test-only grid-readback hook).

**Invariants:** regenerating any golden locally is byte-identical (determinism); the allowlist only ever shrinks (CI diff check).
**Rejection triggers:** timing-dependent replays; goldens regenerated wholesale in a PR without justification.
**Required tests:** the harness *is* the deliverable — red/green on the interim engine with the allowlist checked in.

---

## P2-05 — `GitLoomOS` bootstrapper (G-7.2a)

**Milestone:** M6 · **Priority:** P0 · **Depends on:** P2-02 (daemon to launch); installer payload arrives with P2-21.

Contract summary (strategy §G-7.2a, binding): `GitLoomOsBootstrapper` (client-side) — detect `GitLoomEnv` via `wsl.exe --list --quiet`; import from versioned tarball if absent; **merge, never clobber** `%UserProfile%\.wslconfig` (INI parse, add only our keys under `[wsl2]`, back up first; defaults `memory=min(50% RAM, 8GB)`, `autoMemoryReclaim=gradual`); first-boot: raise `fs.inotify.max_user_watches`, start `dockerd` via `/etc/wsl.conf` boot command, wait for the socket; launch `gitloomd`, health-check gRPC, staged-checklist progress UI; **idempotent** — every step checks-then-acts, partial bootstrap resumes.

**Edge cases:** existing user `.wslconfig` keys preserved (fixture-tested INI merger); `wsl --terminate` mid-bootstrap → next start resumes; WSL not installed → actionable failure (P2-21 owns enablement).
**Invariants:** never `wsl --shutdown` (G-12); re-run is a no-op; other distros untouched (uninstall test).
**Required tests:** INI-merger fixtures; state-machine unit tests per step (check/act seams mocked); manual matrix in the PR (fresh import < 60 s, `docker info` green inside the VM, kill-VM recovery).

---

## P2-06 — Repo provisioner: the Git-native sync boundary (G-7.2b)

**Milestone:** M6 · **Priority:** P0 — the data path every agent depends on · **Depends on:** P2-02, P2-05.

### Contract

```csharp
// daemon-side GitLoom.Core/Agents/RepoProvisioner.cs
public sealed record ProvisionResult(string RepoHash, string BareRepoPath, string VmRemoteUrl);
public interface IRepoProvisioner
{
    ProvisionResult Provision(string windowsRepoPathNormalized);   // clone-or-fetch the ext4 bare mirror
}
// daemon-side WorktreeManager.cs
public interface IAgentWorktreeManager
{
    string CreateAgentWorktree(string repoHash, string agentId);   // branch agent/<id> from main + worktree
    void RemoveAgentWorktree(string repoHash, string agentId, bool force);
    void Prune(string repoHash);
}
```

Windows side: on project open, register remote `gitloom-vm` → `\\wsl.localhost\GitLoomEnv\...\repos\<hash>.git` (idempotent; via existing `AddRemote`).

### Implementation steps

1. `<hash>` = SHA-256 of the normalized Windows repo path → `~/gitloom/repos/<hash>.git`; first provision `git clone --bare /mnt/c/...` (9P acceptable for object transfer only — file *watching* over 9P is what's forbidden), subsequent `git fetch`.
2. `core.untrackedCache=true` in the bare template; worktrees under `~/gitloom/worktrees/<repo>/<agentId>` on branch `agent/<id>`.
3. All git via the F2 runner compiled into the daemon (same `RunGit` family, same redaction).
4. `pnpm install` post-worktree when `pnpm-lock.yaml` exists (content-addressable store → N agents ≈ 1× disk).
5. Expose through `RepoSyncService` (replaces the P2-02 stubs).

### Edge-case matrix

| Case | Required behavior |
|---|---|
| second provision of the same repo | incremental fetch, no re-clone (test measures) |
| Windows repo path with spaces/Unicode | hash + UNC registration correct |
| worktree add on an already-used agent id | typed failure |
| bare repo manually deleted | next provision re-clones cleanly |
| `RemoveAgentWorktree(force: false)` on a dirty worktree | typed failure; `force: true` succeeds |

### Invariants (MUST)

1. **G-11:** no container ever mounts a Windows path — the ext4 worktree is the only mount source (asserted in P2-07's inspect test, plumbed here).
2. An agent commit in the VM worktree reaches the Windows repo byte-identically via `git fetch gitloom-vm && git merge agent/<id>` (round-trip test).
3. Provisioner and worktree manager are daemon services with no UI dependencies.

### Rejection triggers

- Any bind mount of `/mnt/c` into agent-visible paths.
- Worktrees on the Windows filesystem "temporarily".

**Required tests (Linux CI):** provision → bare exists; incremental second run; worktree add/remove/prune round-trip; Windows↔VM commit round-trip (fixture repos both sides); path-with-spaces.

---

## P2-07 — Sandbox hardening + default-deny egress (G-7.2c)

**Milestone:** M6 · **Priority:** P0 launch-tier security — the primary prompt-injection exfiltration control · **Depends on:** P2-05, P2-06.

Contract summary (strategy §G-7.2c, binding — plus market promotion to launch tier): `SandboxEngine` (Docker.DotNet `CreateContainerAsync`): static base image with Nix/Devbox (**no runtime `docker build`**, G-16); `no-new-privileges`, userns remap, default seccomp, memory+pids limits, worktree mount from ext4 only, tmpfs `/dev/shm` for credentials (P2-01 injector content, mode 0400), read-only rootfs where tolerated. `EgressProxyConfigurator`: internal network whose only route out is a proxy container; default-deny; allowlist = model APIs + package registries + the repo's git host; DNS pinned to the proxy; `HTTP(S)_PROXY` env **and** iptables DROP on direct egress. Per-repo persistent jail (`docker start` if stopped). Allowlist user-visible/editable; changes logged (feeds P2-17).

**Edge cases:** allowlisted `curl https://api.anthropic.com` succeeds via proxy; `curl https://example.com` fails fast (refused, not timeout); direct-IP egress fails; DNS exfil (`dig x.attacker.tld`) fails; `devbox add jq` during a live PTY session survives.
**Invariants:** G-11/G-15/G-16; every verification bullet evidenced in the PR description; credential tmpfs is per-agent — no `~/.claude`/global auth-dir mounts, ever.
**Acceptable variations (MAY):** offering **Docker Sandboxes (sbx)** as an optional "maximum isolation" backend behind the same `SandboxEngine` interface (microVM + its Locked-Down egress preset, GA on Windows since 2026-01) — the native WSL2 path stays the zero-extra-install default, and the egress/audit invariants apply to both backends.
**Rejection triggers:** proxy-env-only enforcement (no iptables backstop); a "temporary" `--privileged`; making sbx a hard dependency.
**Required tests:** container-spec builder unit tests (flags asserted on the create request); egress matrix as an integration suite tagged `RequiresDocker`; `docker inspect` assertions (no Windows paths, userns, limits).

---

## P2-08 — AI Gateway + admission control + swarm reconciler (G-7.2d) — **launch-blocking**

**Milestone:** M6 exit · **Priority:** P0 (market: without it the first session of the headline feature is a retry storm) · **Depends on:** P2-01, P2-07.

### Contract

```csharp
// daemon GitLoom.Core/Agents/AiGateway.cs
public interface IAiGateway
{
    Task<GatewayLease> AcquireAsync(string agentId, int estimatedTokens, CancellationToken ct); // FIFO within priority
    void Report429(string agentId, TimeSpan? retryAfter);
    GatewaySnapshot GetSnapshot();      // per-agent spend, queue depth, current limits
}
// AdmissionController.cs: bool CanSpawn(out string reason)  — VM memory sampled ≤5s, threshold default 85%
// SwarmReconciler.cs: reconcile Docker (sole source of truth — no lockfiles) against expected agents on boot
```

### Implementation steps

1. Token-bucket (requests + tokens/min seeded from the P2-01 health check), FIFO queue per priority class; leases released on completion with actuals.
2. **429 interception:** the model host is only reachable via the egress proxy route that the gateway fronts; on 429/`Retry-After` → pause the worker's PTY input, mark `RateLimited`, exponential backoff, resume — the CLI process never sees the 429.
3. Budgets: per-agent/per-day token+cost caps; exhausted → typed rejection surfaced in UI; spend telemetry streamed over `GatewayService`.
4. Admission: `/proc/meminfo` sampling; block spawn above threshold with the honest "4–6 agents on 16 GB" message; headroom in `ListAgents` metadata.
5. Reconciler on daemon boot: dead container → prune worktree, mark `Dead`; orphan live container → adopt-or-stop per policy.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| two agents, one key, sustained load | both proceed, fairness (neither starves), zero CLI crashes |
| 429 with `Retry-After: 5` | worker paused, resumes ≈5 s, CLI saw a delayed 200 |
| budget exhausted mid-task | agent paused with a typed reason, not killed |
| daemon reboot with 3 live containers, 1 dead | reconcile adopts 3, prunes 1, UI shows `Dead` disposal |
| memory ≥ threshold | spawn rejected with typed reason; existing agents unaffected |

### Invariants (MUST)

1. No agent process ever observes a raw 429 (integration-asserted with a fake model endpoint).
2. Bucket math is pure and property-tested (burst, refill, fairness).
3. Reconciler trusts Docker state only — PID files/lockfiles are a rejection trigger.

**Required tests:** bucket/backoff/budget unit suites; fake-429 endpoint integration; memory-pressure simulated spawn rejection; out-of-band `docker rm` → boot reconcile outcome.

---

## P2-09 — Agent lifecycle: cooperative yield + keep-alive rebase (G-7.3 part 1)

**Milestone:** M7 · **Priority:** P0 · **Depends on:** P2-06, P2-07.

Contract summary (strategy §G-7.3 steps 1–2, 7–8, binding): **Cooperative Yield Protocol** — `[IPC_UPDATE_REQUESTED]` to the agent's control channel, await `[IPC_UPDATE_READY]` (timeout → `docker pause`); only then touch the worktree; guard every Git mutation (abort if mid-rebase or detached HEAD; exponential-backoff retry on `index.lock`). **Keep-alive rebase** — yield → `add -A && commit -m "wip: sync" && rebase main` → resume; conflicts → status `Conflict` + route to the T-04 resolver against the worktree. **Session durability** — PTYs under a persistent session leader in the VM; daemon restart reattaches (leader registry reconciled like P2-08). **Teardown** — `IDisposable` agent context: kill PTY, `worktree remove --force`, `branch -D agent/<id>`, close floating dock windows; filesystem verified clean.

**Edge cases:** yield timeout → pause path exercised; keep-alive with agent mid-`git rebase` of its own → skipped (guard) and retried next cycle; leader survives daemon kill -9 (reattach test).
**Invariants:** the human's live edits reach agent worktrees only via Git (keep-alive rebase), never file sync; no Git mutation while the agent is unpaused/unyielded.
**Rejection triggers:** touching a worktree without a completed yield; polling `ps` for agent liveness (Docker is truth).
**Required tests:** scripted-container yield round-trip; keep-alive conflict → `Conflict` status; teardown residue check (`git worktree list` + `docker ps -a` clean); leader reattach.

---

## P2-10 — Merge queue + verification runs + stale invalidation (G-7.3 part 2 + market D-1) — **the product spine**

**Milestone:** M7 · **Priority:** P0 — this is the lead feature per the July-2026 viability research · **Depends on:** P2-09.

**Positioning (vs "just use GitHub's merge queue"):** GitHub's server-side queue is the only shipping stale-invalidation analogue — but it is CI-bound, PR-only, GitHub-hosted, and agent-blind. This queue works **pre-PR, locally, across N agent branches, without CI round-trips, and on any host** — no desktop or orchestration product ships that (verified 2026-07-07).

### Contract

```csharp
// daemon GitLoom.Core/Agents/Orchestrator/MergeQueue.cs
public enum WorkerMergeState { Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected }
public sealed record VerificationRecord(string AgentId, string MainSha, bool Passed, string LogArtifactPath, DateTimeOffset When);
public interface IMergeQueue
{
    WorkerMergeState GetState(string agentId);
    Task<VerificationRecord> RunVerificationAsync(string agentId, CancellationToken ct); // test cmd in the agent's sandbox
    void NotifyMainMoved(string newMainSha);        // marks every Verified worker StaleVerified + auto re-queues
    bool CanMerge(string agentId, out string reason); // false when stale/unverified (settings override, loudly labeled)
}
```

Windows side: `ForegroundMergeService` — "Merge to Main" = `git fetch gitloom-vm && git merge agent/<id>` on the Windows repo (human-gated, journaled via T-19); post-merge installs run `--ignore-scripts` wrapped in retry (NTFS `EPERM`/`EBUSY`).

### Implementation steps

1. State machine exactly as the enum; transitions persisted (daemon SQLite) so a daemon restart resumes queue state.
2. Verification = the project's configured test command run in the worker's own sandbox; record `main@<sha> + pass/fail + log artifact`.
3. **Stale cascade:** every merge to main → `NotifyMainMoved` → all `Verified` workers flip `StaleVerified` and auto re-enter (yield → keep-alive rebase → re-verify). This loop is the single hardest coordination problem of parallel agents and the moat — it gets the densest tests in the milestone.
4. Merge gating: `CanMerge` false on stale/unverified; the override setting exists but renders a loud warning and is journaled + audited.
5. Rejection path: branch delete, sandbox prune, teardown per policy.
6. Works identically when the "worker" is an external PR intake (P2-12) — the queue keys on a branch, not on a PTY.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| A merges while B and C are `Verified` | B, C → `StaleVerified`, auto re-queue, re-verify against new main |
| verification fails after rebase | worker back to `Working` with the failure surfaced, not silently retried |
| merge attempted on stale verification | blocked; override path logged + audited + labeled |
| daemon restart mid-`Verifying` | run restarts or resumes; state never stuck |
| test command absent | typed "no verification command configured"; merge allowed only with the explicit unverified override |

### Invariants (MUST)

1. A merge through the UI on a fresh `Verified` state is the only silent path; every other path warns and records.
2. Verification results are immutable records tied to a `main@<sha>`; re-verification creates a new record.
3. The human foreground merge happens on the Windows repo via the existing journaled service surface (undoable via T-19).

### Rejection triggers

- Auto-merge of any kind — the human gate is the product thesis.
- Verification run outside the worker's sandbox (host execution).

**Required tests:** exhaustive state-machine unit suite incl. the stale cascade + override; two-scripted-worker integration (A merges → B re-verifies → merge button blocked until fresh); restart-resume; `--ignore-scripts` canary (poisoned `postinstall` does not execute).

---

## P2-11 — Review cockpit: risk-ranked diffs, per-hunk provenance, flagged-changes gate (market D-2)

**Milestone:** M7 · **Priority:** P0 (the daily-driver reason to open GitLoom; review time +91% is the buying trigger) · **Depends on:** P2-10; reuses T-06 `PatchParser`, T-11 blame, T-13 diff stack.

### Contract

```csharp
// GitLoom.Core/Review/RiskClassifier.cs (pure)
public enum RiskCategory { ExecutableConfig, Lockfile, CiWorkflow, GitHooks, EditorConfig, SecuritySensitivePath, Source, Docs }
public sealed record HunkRisk(RiskCategory Category, int Rank);   // lower rank = review first
public static class RiskClassifier
{
    public static HunkRisk Classify(string filePath, DiffHunk hunk);   // path + content rules
}

// GitLoom.Core/Review/ProvenanceReader.cs (pure)
public sealed record HunkProvenance(string? Agent, string? Task, string? Plan, string Sha, string Source); // Source: "agent-trace" | "trailer"
public static class ProvenanceReader
{
    /// <summary>Primary: Agent Trace records (the Cognition/Cursor interchange standard —
    /// JSON trace records mapping file/line ranges to contributors). GitLoom both emits them
    /// from the orchestrator and renders them; first review UI to do so.</summary>
    public static IReadOnlyList<HunkProvenance> FromAgentTrace(string traceJson);
    /// <summary>Fallback for non-emitting agents: Agent:/Task:/Plan: commit trailers.</summary>
    public static HunkProvenance? FromTrailers(string commitMessage, string sha);
}

// GitLoom.Core/Agents/Orchestrator/FlaggedChangeDetector.cs (pure)
public static class FlaggedChangeDetector
{
    /// <summary>Paths/hunks that require explicit acknowledgment before the merge button enables.</summary>
    public static IReadOnlyList<(string Path, RiskCategory Category)> Detect(IReadOnlyList<FilePatch> mergeDiff);
}
```

UI: the agent-branch diff view orders files/hunks by risk rank (not alphabetically); a provenance gutter chip per hunk (agent · task · plan); a pairing-style **\"bring this branch local\"** action (fetch the agent branch into a local worktree via the T-29 plumbing — the hand-back UX Sculptor's Pairing Mode proved); a distinct flagged-changes panel that must be acknowledged item-by-item before `CanMerge` consults it; a test-delta strip (what the P2-10 verification newly covers/failed vs main).

### Implementation steps

1. `RiskClassifier` rules: `package.json` **scripts block** edits (content rule — a dependency bump alone is `Lockfile`, not `ExecutableConfig`), lockfiles, `.github/workflows/`, git hooks, `.vscode/`, path heuristics for security-sensitive code (auth/, crypto/, *Security*).
2. Provenance is dual-source: the orchestrator (P2-09/P2-14) **emits Agent Trace records** for every worker session (and writes `Agent:`/`Task:`/`Plan:` trailers as the durable in-history fallback); `ProvenanceReader` consumes trace JSON first, trailers second. Rendering the standard that Cursor/Jules/Amp emit makes external branches (P2-12) attributable too.
3. Cockpit view composes existing controls: T-13 diff rendering, risk ordering, provenance chips, acknowledgment panel; acknowledgments recorded (audited later by P2-15).
4. Wire `FlaggedChangeDetector` output into P2-10's `CanMerge`.

### Edge-case matrix

| Case | Required behavior |
|---|---|
| dependency bump only | flags `Lockfile`, not `ExecutableConfig` |
| script added to `package.json` | flags `ExecutableConfig`; acknowledgment required |
| commit without trailers (human commit) | provenance chip absent, no crash, rank still applies |
| renamed file with risky content | classified by new path + content |
| acknowledgment then diff changes (new push) | acknowledgments reset (they bind to a diff hash) |

### Invariants (MUST)

1. Classifier/detector/reader are pure and fixture-tested; UI contains no rule logic.
2. Acknowledgments bind to the content hash of the flagged hunk set — any change invalidates them.
3. Risk ordering never hides hunks (ordering only; everything remains reachable).

**Rejection triggers:** rules implemented in XAML/code-behind; acknowledgment as a single global checkbox.

**Required tests:** classifier fixture corpus (each category + the scripts-vs-bump distinction); trailer parse matrix; acknowledgment-invalidation; end-to-end: poisoned postinstall branch → panel appears → merge blocked until acknowledged (extends the P2-10 canary).

---

## P2-12 — External agent PR intake (new; market D-1 "vendor-neutral moat")

**Milestone:** M7 (accelerated 2026-07-07: Jules ships a public API + GitHub Action, Codex/Devin PR volume is compounding, and Kepler's PR-based tasks show competitors circling — the vendor-neutral square is still empty) · **Priority:** P0 · **Depends on:** P2-10; reuses T-23 (PR list), T-29 (PR → worktree).

### Why

Teams already run Codex/Jules/Copilot cloud agents that only surface PRs. Subscribing those PRs into the same verify→review→merge pipeline makes GitLoom useful on day one without anyone changing how they run agents — the cheapest wedge in the market research, and one no competitor ships.

### Contract

```csharp
// GitLoom.Core/Agents/Orchestrator/ExternalPrIntake.cs (daemon)
public sealed record ExternalPrSource(string Host, string Owner, string Repo, string? AuthorFilter); // e.g. bots
public interface IExternalPrIntake
{
    void Subscribe(ExternalPrSource source);
    /// <summary>Poll: new/updated open PRs matching the filter → materialize each as a queue entry
    /// (fetch PR head into the VM bare repo as agent/pr-<n>, worktree, enter MergeQueue at Working).</summary>
    Task PollOnceAsync(CancellationToken ct);
}
```

### Implementation steps

1. Reuse `IPullRequestService` (T-23) for listing; author-filter for known bot accounts (configurable list, e.g. `codex[bot]`, `google-jules[bot]`, `copilot`).
2. Materialize: `git fetch origin pull/<n>/head:agent/pr-<n>` into the VM bare repo (authenticated CLI path), create the worktree, enter the P2-10 queue at `Working` → verification runs exactly as for local agents.
3. Review happens in the P2-11 cockpit; **merge is pushed back through the host PR merge API** (T-23 merge) rather than a local foreground merge — the queue's merge step is pluggable per entry origin.
4. PR updates (new commits) re-enter the queue (stale semantics identical).

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| PR force-pushed | old verification invalidated, re-queued |
| PR closed upstream mid-queue | entry cancelled, worktree pruned |
| same PR subscribed twice | idempotent |
| rate limits | polls go through the host client's typed error path; backoff, never a crash loop |

MUSTs: intake writes nothing to the upstream PR without an explicit user action (review submit / merge click); token usage stays inside the audited T-23 transport.
**Required tests:** fixture-driven poll → queue-entry materialization; force-push invalidation; closed-PR cleanup; merge-path dispatch (local vs host-API) unit-tested.

---

## P2-13 — Activity bar & docking UI (G-7.4)

**Milestone:** M7 · **Priority:** P0 · **Depends on:** P2-02, P2-03.

Contract summary (strategy §G-7.4, binding): `Dock.Avalonia` workspace (Terminal + agent-diff + staging per agent, layout persisted); Activity bar — Row 0: Resource Monitor (VM CPU/RAM sparklines + token-spend counters from `GatewayService`) + pinned tabs incl. Coordinator with `IsAttentionRequired` pulse; Row 1: virtualized LIFO agent list. Status micro-badges via one `AgentStatus → Brush` converter (theme tokens — all five themes). OS notifications on transitions into waiting/blocked (suppressed when foregrounded on that agent). Teardown discipline: `IDisposable` sandbox VMs, timers stopped, floating dock windows closed (documented Dock.Avalonia leak), `WeakReferenceMessenger` only.

**Invariants:** open/close an agent tab 50× → stable heap + zero floating windows (blocking memory test); all colors via design tokens (v1 UI rules apply unchanged).
**Required tests:** status→brush mapping; LIFO ordering; attention derivation; the 50× memory harness; headless PNG of the bar with 4 fake agents in every theme.

---

## P2-14 — Plan approval + dual-mode orchestration (G-7.5)

**Milestone:** M7 · **Priority:** P0 — the product thesis · **Depends on:** P2-08, P2-09, P2-13.

Contract summary (strategy §G-7.5, binding, with the market promotion of plan-approval into the headline): `CoordinatorAgent` — chat agent with **no code, no worktree, no merges**; tools `spawn_worker(taskSpec)`, `get_worker_status`, `send_worker_prompt`, `request_verification`, capped by limits/budgets/admission. **Two-phase spawn:** structured `TaskPlan { Scope: files[], Approach, TestStrategy }` (JSON-schema validated) → rendered for approval → **workers start only on approved plans**; plan + approver OS identity persisted (P2-15 chains it). Terminal locking for managed workers enforced **daemon-side** (input stream severed at the gRPC layer, not just UI read-only). **Kill switch:** yield-all (timeout → `docker pause`) + queue freeze + journal snapshot; one always-visible control. Human handoff: `AwaitingReview` badge; merges only via the P2-10 human path. Coordinator serializes dependent tasks; partitioning quality is tracked telemetry.

**Edge cases:** plan rejected → worker never spawns, no worktree residue; kill switch with an agent mid-yield → pause after timeout; manual-mode spawn bypasses coordinator but not admission/budgets.
**Invariants:** input-lock verified at the gRPC layer by test (hand-crafted client rejected); kill switch freezes all containers < 5 s; the coordinator cannot invoke merge RPCs (interceptor-enforced role, not convention).
**Required tests:** spawn-cap/budget rejection; plan schema validation corpus; scripted-coordinator end-to-end (2 independent tasks → parallel workers → verified → sequential human merges with a stale re-verify between); kill-switch fan-out ordering.

---

## P2-15 — Tamper-evident audit log (H-8.2, pulled forward)

**Milestone:** M7.5 — target **before 2026-08-02** (EU AI Act enforcement window) · **Priority:** P0 for enterprise, P1 overall · **Depends on:** P2-14 approval records exist; start once P2-10 is merged.

> **Scope split (2026-07-07):** ship the **evidence pack** first — hash chain + authorizing identity + `gitloomd audit verify` + the P2-16 SIEM feed; RFC 3161 external anchoring (step 3) may land as a fast-follow. Claims language is "audit-grade / tamper-evident": Article 12 mandates automatic logging and traceability, **not** cryptography — hash-chaining is the differentiator, not a legal checkbox. Standalone audit vendors (Agent Audit, Asqav, Compliora) prove the demand but none can attribute actual code changes — the Git side is unclaimed.

### Contract

```csharp
// GitLoom.Core/Audit/HashChain.cs (pure)
public sealed record AuditRecord(long Seq, DateTimeOffset Timestamp, string Type, string PayloadJson, string PrevHash, string Hash);
public static class HashChain
{
    public static string ComputeHash(string prevHash, string canonicalPayload);   // SHA-256(prevHash ‖ payload)
    public static (bool Valid, long? FirstBadSeq) Verify(IEnumerable<AuditRecord> records);
}

// GitLoom.Core/Audit/AuditLog.cs (daemon)
public interface IAuditLog
{
    long Append(string type, object payload, string osIdentity);   // canonicalizes, chains, persists
    IReadOnlyList<AuditRecord> Read(long fromSeq, int take);
    (bool Valid, long? FirstBadSeq) VerifyAll();
    long Redact(long seq, string reason, string osIdentity);       // new chained event referencing the original's hash — never rewrites
}
```

Event types (minimum): `inference` (model, prompt, output), `agent_spawned`, `agent_stopped`, `plan_approved`, `plan_rejected`, `merge_approved`, `merge_rejected`, `stale_override_used`, `egress_denied`, `budget_exceeded`, `killswitch`, `acknowledged_flagged_change`, `redaction`.

### Implementation steps

1. Canonical JSON (sorted keys, invariant culture) → `HashChain.ComputeHash`; SQLite append-only table + an append-only file mirror.
2. Wire `Append` at every Gateway/lifecycle/approval/merge touchpoint (G-17 becomes hash-chained here).
3. **External anchoring:** every N records / 24 h, RFC 3161 timestamp the head hash (`Rfc3161Anchor.cs`); store the TSA token; `gitloomd audit verify` walks the chain + validates anchors.
4. AES-GCM at rest (key in OS keyring), retention default 90 d, redaction as a chained event.
5. Full prompt/output logging is a sensitive store: encryption, retention, and redaction are part of the feature, not afterthoughts (market v2 §6).

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| single-byte tamper anywhere | `VerifyAll` fails at exactly that seq |
| redaction | payload replaced, chain still verifies (redaction event carries the original hash) |
| daemon crash mid-append | no torn record (transactional write); chain resumes |
| TSA unreachable | anchoring queued/retried; log keeps appending (anchoring is best-effort, chaining is not) |

MUSTs: `HashChain` pure + property-tested; no plaintext prompt content on disk outside the encrypted store; every G-17 touchpoint emits exactly one event (idempotence per operation).
**Rejection triggers:** rewriting or deleting records under any code path; hashing non-canonical JSON.
**Required tests:** tamper detection sweep; redaction verifiability; anchor round-trip (network-gated trait); touchpoint coverage test (run a scripted swarm session → assert the expected event sequence).

---

## P2-16 — SIEM exporter (H-8.3)

**Milestone:** M7.5 · **Priority:** P1 (enterprise) · **Depends on:** P2-15.

Contract summary (strategy §H-8.3, binding): `SiemExporter` streaming P2-15 events as CEF/JSON over syslog (TCP/TLS), Splunk HEC, and generic webhook; per-sink config, buffering + retry with a bounded queue, delivery-status panel; event taxonomy documented in `docs/siem-events.md`.

**Invariants:** sink outage → buffered redelivery, zero loss up to the cap, loud state past it; schema-valid JSON (JSON-schema test); 1k events/min load test.
**Required tests:** local syslog container + mock HEC integration; outage/redelivery; schema validation corpus.

---

## P2-17 — Source-available trust architecture + network transparency (H-8.1)

**Milestone:** M7.5 · **Priority:** P0 for enterprise GA (licensing already LOCKED: FSL backend / proprietary GUI+Coordinator) · **Depends on:** P2-07 (proxy logs).

Contract summary (strategy §H-8.1, binding): repo split enforcing the license boundary (daemon + sandbox/worktree engine + adapters → FSL repo publishing NuGet artifacts; GUI/Coordinator/governance stay private and pin versions); published `docs/security-architecture.md` living in the FSL repo next to the code it describes; **network transparency view** — in-app panel streaming every outbound connection from daemon + sandboxes (source = egress proxy logs): destination, agent, bytes, verdict, filterable, exportable; independent security audit commissioned pre-enterprise-GA with a `SECURITY.md` intake.

**Invariants:** license headers/`LICENSE` correct per artifact (CI check); the transparency view shows a live allowed call and a denied attempt within seconds; every doc claim maps to a test or config reference.
**Required tests:** CI license check; proxy-log → view-model streaming integration; doc-claims checklist in the PR.

---

## P2-18 — Terminal target engine: server-side libvterm + Skia grid renderer (G-7.1b)

**Milestone:** M7 (before beta) · **Priority:** P0 · **Depends on:** P2-04 green on the interim engine. **Gate:** P2-04 ≥ parity on libvterm — no golden regression.

Contract summary (strategy §G-7.1b, binding): P/Invoke bindings (`vterm_new`, `vterm_input_write`, screen callbacks incl. `sb_pushline`/`sb_popline`, keyboard encoders); one `VtermSession` per agent PTY owned by the session leader; damage rects coalesced by the 16 ms ticker into `GridUpdate` protos (cell runs: UTF-32 glyph + combining, truecolor fg/bg, attr bitset; cursor; scroll ops first-class); snapshot/attach path (full grid + modes + lazy scrollback) serving crash recovery, reattach, and future cloud; `TerminalGridControl` — first-party Skia cell grid (glyph-run cache, damage-only redraw, selection/clipboard, IME overlay, CJK double-width, mouse/keyboard encoders incl. bracketed paste); engine behind `TerminalEngine=libvterm|interim` flag until P2-04 signs off; linux-x64 `libvterm.so` built in CI from pinned source, daemon-side only.

**Invariants:** kill client mid-`htop` → reattach renders an identical grid; daemon restart with leader alive → live reattach; sustained 50 MB `cat` keeps client CPU bounded with no full-grid sends in steady scroll; Claude Code/vim/htop/tmux manual matrix.
**Required tests:** P2-04 suites on this engine (the merge gate); snapshot/attach integration; damage-coalescing perf measurement in the PR.

---

## P2-19 — Cross-worktree conflict radar (new; market D-5)

**Milestone:** M7.5 · **Priority:** P1 — a visible differentiator no competitor ships · **Depends on:** P2-06 (worktrees), T-02 chunker (already on main).

### Why

GitKraken markets "predictive conflict detection" between *a user's* branches. Nobody watches N live agent worktrees against each other **and** main and warns *before* either merges. GitLoom has every ingredient: the daemon owns all worktrees, and the pure 3-way chunker classifies overlap.

### Contract

```csharp
// daemon GitLoom.Core/Agents/ConflictRadar.cs
public sealed record OverlapWarning(string AgentA, string AgentB, string Path, bool CertainConflict); // certain = same-line
public interface IConflictRadar
{
    /// <summary>Pairwise diff of live agent branches (and each vs main): file-level overlap
    /// plus line-level certainty via the T-02 chunker on the overlapping files.</summary>
    IReadOnlyList<OverlapWarning> Scan(string repoHash);
    event Action<OverlapWarning>? NewOverlap;    // raised by the scheduled scan on new findings
}
```

### Implementation steps

1. Per scan: `git diff --name-only main...agent/<id>` per branch (CLI, bare repo); file-set intersections per pair → candidate paths.
2. For candidates, run `GenerateMergeChunks(base, oursText, theirsText)` (blob texts from the bare repo) — any `Conflict` chunk ⇒ `CertainConflict = true`; same file, disjoint chunks ⇒ soft warning.
3. Scheduled after each keep-alive rebase cycle (piggyback P2-09's cadence — no extra yields; reads only refs/blobs, never worktree files).
4. Surface: badge on both agent cards (P2-13), a radar panel listing pairs/paths, and a `stale`-style hint in the P2-10 queue ("merging A will conflict B").

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| binary file overlap | file-level warning only, never chunk classification |
| agent branch identical to main | no self-noise |
| 6 agents (15 pairs) on a large repo | scan bounded: name-only diffs first, chunker only on intersections; measured in the PR |
| overlap disappears after a rebase | warning cleared |

MUSTs: radar is read-only (never touches worktrees or locks the index — bare-repo object reads only); pure classification logic separated from the git plumbing for unit tests.
**Rejection triggers:** scanning working trees directly; running chunker on every pair×file without the name-only prefilter.
**Required tests:** fixture bare repo with three branches (certain conflict, same-file-disjoint, no overlap) → exact warning set; clearing on rebase; binary handling.

---

## P2-20 — Agent commit-stream curation (new; market D-5)

**Milestone:** M7.5 · **Priority:** P1 · **Depends on:** P2-09; reuses T-08 `InteractiveRebaseService` + T-31 conventional-commit builder (both on main).

### Why

Agents produce checkpoint noise ("wip: sync", 40 micro-commits). Reviewers need reviewable history. A one-click "squash agent checkpoints into N reviewable conventional commits" is pure Git surgery — exactly what a wrapper tool cannot build without re-implementing a Git client.

### Contract

```csharp
// GitLoom.Core/Agents/Orchestrator/CommitCurator.cs
public sealed record CurationPlan(IReadOnlyList<RebaseTodoItem> Todo, string Summary);
public static class CommitCurator     // pure planner; execution goes through IInteractiveRebaseService
{
    /// <summary>Folds wip/checkpoint commits into their nearest meaningful ancestor and rewords
    /// surviving messages to conventional-commit form (via ConventionalCommitBuilder).</summary>
    public static CurationPlan Plan(IReadOnlyList<(string Sha, string Message)> branchCommits, CurationOptions options);
}
```

UI: on an `AwaitingReview` agent branch — "Curate history" preview (before/after commit list) → executes via the existing T-08 engine against the worktree (yielded, P2-09 discipline) → verification re-runs (history rewrite ⇒ stale by definition).

### Edge cases / invariants

| Case | Required behavior |
|---|---|
| branch of only wip commits | single squashed commit with a generated conventional subject |
| merge commit in range | curation refused (same T-08 v1 restriction), typed |
| curation while agent running | blocked — only `AwaitingReview`/paused branches |
| post-curation | P2-10 marks the branch unverified; re-verify before merge |

MUSTs: planner is pure and fixture-tested; execution exclusively via `IInteractiveRebaseService` (no second rebase driver — G-7 heritage); provenance trailers (P2-11) preserved onto squashed results.
**Required tests:** planner fixtures (wip folding, reword mapping, trailer preservation); integration on a fixture worktree branch; staleness handoff.

---

## P2-21 — Installer part 1: diagnostics, OS enablement, payload pipeline (J-1…J-3)

**Milestone:** M7 · **Priority:** P0 for distribution · **Depends on:** P2-05 (shares bootstrap code).

Contract summary (strategy §§J-1–J-3, binding): `SystemDiagnostics` (Win11 x64 build check, WMI virtualization flags, WSL2 state parse, ≥20 GB disk; each check `Pass | Fail(actionable message + doc link)`; hard-stop before any system modification; ARM64 → explicit unsupported gate); unelevated OOBE with UAC only at "Construct Sandbox" (elevated helper relaunch), `Enable-WindowsOptionalFeature` with the raw PowerShell surfaced, reboot-resume via an **elevated Scheduled Task** (never `RunOnce`) + `oobe-state.json`; reproducible `GitLoomOS.tar.gz` build (`build/gitloomos/`, versioned `/etc/gitloomos-release`), silent import reusing P2-05, in-place VM upgrade path preserving provisioned repos, documented CVE patch cadence (`docs/gitloomos-updates.md`).

**Invariants:** WSL-status parsers fixture-tested against captured outputs per WSL version; tarball hash-stable given pinned inputs; vN→vN+1 upgrade preserves repos/worktrees (automated test).
**Required tests:** parser fixtures; INI/state-machine units; CI tarball build; VM-snapshot manual matrix in the PR.

---

## P2-22 — Installer part 2: Windows integration, loopback OAuth, adapter channel, teardown (J-4…J-6)

**Milestone:** M7 · **Priority:** P0 · **Depends on:** P2-21.

Contract summary (strategy §§J-4–J-6, binding): Explorer context menus (install-written, uninstall-removed); **RFC 8252 loopback + PKCE** for every token flow (shared `LoopbackOAuthListener`: ephemeral port, `state` validation, single-use, 5-min timeout); `gitloom://` handler for **non-secret deep links only**; **pinned adapter channel** — `adapters.json` manifest (cli → version, install cmd, config shims, health probe) fetched from a GitLoom-owned channel, installed inside the VM at pinned versions, never `@latest`, updated independently of app releases (keeps perpetual-fallback licenses functional — market v2 §5.3); clean uninstall (`--terminate` → poll → `--unregister`, registry/tasks/appdata removal, user repo untouched, optional `gitloom-vm` remote removal).

**Invariants:** no token ever in a `gitloom://` URL (grep + code-path test); personal distros untouched by uninstall (G-12); pinned adapter unaffected by a breaking upstream release (simulated test).
**Required tests:** PKCE verifier/challenge + state rejection units; manifest schema; adapter pin simulation; uninstall matrix on a machine with a personal distro (manual, evidenced).

---

## P2-23 — Enterprise access & policy: RBAC / SSO / SCIM (H-8.4)

**Milestone:** M8 · **Priority:** P2 (enterprise GA) · **Depends on:** P2-15, P2-16, P2-22 (loopback OAuth infra).

Contract summary (strategy §H-8.4, binding): role → permission set (`spawn_agents`, `approve_plans`, `approve_merges`, `edit_egress`, `edit_budgets`); OIDC SSO (loopback+PKCE) mapping IdP groups→roles; SCIM 2.0 provisioning endpoint; **enforcement in daemon interceptors** (identity on every gRPC call — UI hiding is not enforcement); signed centralized policy doc (model allowlists, egress rules, budgets) fetched and enforced by Gateway + egress configurator.

**Invariants:** a role without `approve_merges` gets `PERMISSION_DENIED` on the merge RPC even from a hand-crafted client; policy updates propagate without daemon restart; SCIM create/deactivate round-trips against a test harness.

---

## P2-24 — Supply-chain & secrets compliance (H-8.5)

**Milestone:** M8 · **Priority:** P2 · **Depends on:** P2-10 (gate UI), P2-01 (`ISecureKeyStore`).

Contract summary (strategy §H-8.5, binding): Vault KV2 + AWS Secrets Manager backends for `ISecureKeyStore` selectable per org policy; **SCA/license gate at `Verified`** — lockfile-delta extraction (npm/pnpm/NuGet) → SPDX license lookup (local database) → copyleft heuristics flag GPL/AGPL as a blocking review category in the P2-11 flagged panel.

**Invariants:** lockfile-delta extraction fixture-tested per ecosystem; an AGPL-introducing agent branch blocks the merge button until acknowledged; Vault round-trip against a dev-mode container.

---

## P2-25 — Cloud worktrees: guardrails now, implementation post-GA (I)

**Milestone:** continuous + M8 · **Priority:** guardrails P0 (they are CI checks), implementation post-desktop-GA (private beta ≤ 2 quarters after — promoted per market v2 §7.1).

Binding now: every proto stays transport-agnostic (G-14); a WAN-latency CI job (`tc netem` 80 ms) runs the P2-14 end-to-end suite once per release; grid protocol + merge-queue RPCs must pass it unchanged. Implementation when scheduled: daemon container image (same binary), mTLS + user auth replacing the session token, per-tenant encryption at rest, `RemoteEnvironment` picker (local VM | cloud), repo sync via `git push gitloom-cloud` over HTTPS.

**Acceptance:** the unchanged P2-14 suite passing over WAN; terminal echo < 100 ms at 80 ms RTT.

---

## P2-26 — `VibeOrchestrator` engine + stream interception (K-1; UI stays post-v1)

**Milestone:** M8 · **Priority:** P1 (shared architecture — the Coordinator reuses it) · **Depends on:** P2-03, P2-08.

Contract summary (strategy §K-1, binding): daemon service tapping agent-CLI + dev-server PTY streams in memory: dev-server port harvesting (`http://localhost:(\d+)` → `[APP_READY_ON_PORT_X]`), OAuth-URL detection → `[AUTH_REQUIRED]` with `state=<agent_uuid>` (P2-22 loopback flow), error interception (`ERR!`/stack traces → fix prompt into agent stdin, bytes never leave the VM), **circuit breaker** (SHA-256 of normalized trace; 3 identical or 5 errors/10 min → `docker pause` + escalate). Chat bridge RPC. K-2…K-5 (auto-checkpoints, escalation UX, Vibe UI, one-click deploy) remain specified in the strategy doc and are re-specced in a v2.1 of this document when the cloud product is scheduled.

**Required tests:** pattern matcher against recorded transcripts (ANSI stripped); breaker math; scripted crashing dev-server integration.

---

## P2-C1 / P2-C2 / P2-C3 — Client-parity track (Backlog A-1…A-3, elevated)

**Milestone:** any · **Priority:** P1 competitive parity (GitKraken/Fork power features; C3 doubles as the swarm control surface).

These three follow `docs/GitLoom_Backlog.md` §A sketches with v1 conventions (typed errors, async commands, interface-first, tests-with-PR, journal integration where HEAD moves):

- **P2-C1 Interactive bisect assistant:** `StartBisect/MarkGood/MarkBad/MarkSkip/ResetBisect` (CLI via `RunGitChecked`, pure `BISECT_LOG` parser, `BisectState` with steps-left), wizard UI with Good/Bad/Skip + progress + culprit card (T-32 context), journaled HEAD moves, dirty-tree refusal. Offline-verifiable end-to-end.
- **P2-C2 Global fuzzy search:** `ISearchAggregator` fanning to commits/branches/tags/files + host PRs/issues (T-23/T-24) with the T-18 `FuzzyMatcher`, merged ranking, debounce; `Ctrl+Shift+F` overlay with grouped, highlighted results; Enter jumps.
- **P2-C3 Multi-repo dashboard:** `WorkspaceOverviewService` (branch, ahead/behind, dirty, stash count, last-fetched per registered repo; cached; `RepositoryChanged`/auto-fetch refresh), card grid with Fetch/Pull/Open quick actions, persisted repo set. Later becomes the swarm's home surface (P2-13 integration).

**Required tests:** per the backlog sketches — bisect culprit fixture; aggregator ranking + debounce; overview fixtures (ahead/behind/dirty matrices).

---

# 5. Later phases

- **K-2…K-5 (Vibe product):** auto-checkpoints, escalation UX ("Try different approach / Go back to when it worked / Get help"), Vibe UI + live preview, one-click deploy — cloud-first per the locked sequencing; re-specced when scheduled.
- **Cloud worktrees implementation** (P2-25 step 2) — after desktop GA.
- **Agent skills marketplace, AI CI/CD janitor** — deferred (market v2 §7: require ecosystem scale / merge-gate reputation first).
- **GitLab/Bitbucket/AzDO host-provider implementations** for the T-23…T-28 panels — v1 shipped typed "not yet supported" stubs; scheduled opportunistically as client-parity work.

# 6. What this document deliberately does NOT build

- Generic "spawn N agents" UX beyond parity — commoditized (native in the agent CLIs).
- AI commit messages / chat-with-your-repo — GitKraken owns the checkbox; off-thesis.
- Competing with Codex/Jules on cloud execution — capital-intensive; Phase 9 stays the scaling story, not the pitch.
- 50-agent swarm claims — dishonest on consumer hardware; admission control tells the truth instead.
- Autonomous CI-fix / auto-merge parity with Composio AO / GitHub Agent Merge — the exact opposite of the governed thesis; market **against** it ("AO merges when CI is green; GitLoom proves it's still green *after* everyone else merged").
- Reinventing the hypervisor — Docker sbx / Claude Code sandboxing ship the isolation primitive; GitLoom's product is the integration (worktree + queue + audit + UI) on top.
- Visual-editor breadth (mockups/diagrams à la Nimbalyst) — different buyer.
