# Backlog: agent-image provisioning & daemon observability

*Written 2026-07-17, at the close of the update-mechanism work (#193–#201 + the tier-2 VM upgrade).
These are the two structural gaps that session surfaced and deliberately did not fix. Both were
found the hard way — the field evidence below is from a real install, not speculation.*

---

## 1. `gitloom-agent-base` never reaches installed VMs

### The gap

The hardened jail image every agent container runs (`images/gitloom-agent-base`, P2-07) is built in
CI (`docker build -t gitloom-agent-base:latest images/gitloom-agent-base` in `ci.yml`) and by the
release pipeline — **and nowhere else**. Nothing in the OOBE, the GitLoomOS payload, or any
provisioning step ever loads it into the VM's dockerd. A freshly imported `GitLoomEnv` has an empty
image store, so the **first agent spawn on every real install fails**.

### Field evidence

On a provisioned install with a healthy, current daemon, "Start coordinator" crashed the
`SpawnAgent` handler: the launcher took the real-jail path and Docker threw
`No such image: gitloom-agent-base:latest`. Before the error-mapping fix (#201) this surfaced as a
bare `Unknown — Exception was thrown by handler`; it now surfaces as an actionable
`FailedPrecondition` naming the image — but the spawn still fails. The interim unblock was a manual
in-VM build (`wsl -d GitLoomEnv -- docker build -t gitloom-agent-base:latest <repo>/images/gitloom-agent-base`).

### Constraints on any fix

- **G-16**: no `docker build` at *agent-runtime* (a runtime build severs the agent PTY). A
  provisioning-time build (OOBE, upgrade, or an explicit repair action) does not violate this.
- The image must track its source: a stale image is the same skew class the daemon had before
  tier-1 — whatever ships needs a version/label the daemon can check at spawn preflight.
- The egress-proxy image (`images/gitloom-egress-proxy`) has the same problem and should ride the
  same mechanism.

### Candidate approaches

| Approach | How | Trade-offs |
|---|---|---|
| **A. OOBE/upgrade build step** | A provisioning step (alongside `StartDaemonStep`) runs `docker build` inside the VM from a source tree bundled with the app (or baked into the payload at `/opt/gitloom/images/`). | No huge artifact to ship; needs network at setup time (apt/Nix fetches inside the Dockerfile); minutes-long, needs progress UI; build inputs must be pinned for reproducibility (the Dockerfile already pins). |
| **B. Bundled image tar** | CI `docker save`s the built image; the payload (or the app's `payload/` dir) carries `gitloom-agent-base.tar`; a provisioning step `docker load`s it. | Fully offline + deterministic (the CI-built bytes are what runs); large artifact (hundreds of MB — likely rules out embedding in the GitLoomOS tarball without breaking its size/hash discipline; ship beside it like the daemon payload); trivially versionable via a manifest label. |
| **C. Registry pull** | Publish to a registry (GHCR); the VM pulls at provisioning/first-spawn. | Simplest pipeline; requires network + a public registry story + supply-chain verification (digest pinning); first-spawn pulls re-introduce a long silent wait unless surfaced. |

**Leaning**: B for correctness (CI bytes = runtime bytes, offline installs work), delivered through
the same packaging seam tier-1 uses (`$(GitLoomDaemonPayload)`-style optional MSBuild copy +
warn-if-missing), loaded by a new provisioning step that both the OOBE and the tier-2 upgrade run,
with a daemon-side spawn preflight that checks image presence + version label and returns the
(now-actionable) `FailedPrecondition` naming the repair when absent.

### Acceptance criteria

- A fresh OOBE install spawns its first agent with **zero manual docker commands**.
- The tier-2 VM upgrade leaves a working image in the new distro (rebuilt/reloaded, not assumed
  migrated — the image store lives outside `/home/gitloom` and is *not* covered by the user-data
  migration).
- A version-skewed image is detected at spawn preflight and surfaced actionably (same honesty bar
  as #201), ideally auto-repaired by the same mechanism.
- `gitloom-egress-proxy` ships the same way.

---

## 2. The daemon is observably silent

### The gap

`gitloomd` produces **no log output at all** — not to the journal, not to a file. ASP.NET Core's
default console logging does not survive the daemon's host setup, there is no logging pipeline of
its own, and unhandled RPC handler exceptions vanish (gRPC swallows them into
`Unknown — Exception was thrown by handler` with nothing recorded daemon-side).

### Field evidence (all from one evening)

- The EF migration-lock hang (#194): the daemon sat "active" for hours doing literally nothing;
  `journalctl -u gitloomd` showed only systemd start/stop lines. Diagnosis required a `createdump`
  of the live process and cross-OS `dotnet-dump` analysis — for what one log line
  ("acquiring migration lock…") would have made obvious.
- The missing-image spawn crash (#201): the handler exception was recorded **nowhere**. The only
  trace was the client's `Unknown`.
- Every skew/connectivity incident that evening was diagnosed from *outside* the daemon (ss, proc,
  binary greps) because the daemon could not tell its own story.

### What exists already (client-side, not daemon-side)

The App logs its half to `oobe.log` (`LogOobe`), and #199/#201 made client-facing errors carry the
daemon's `Status.Detail`. The daemon side is still a black box; #201's catch-all mapping is a
mitigation, not observability.

### Proposed shape

- **Journal-first**: systemd already captures stdout/stderr — wire standard
  `Microsoft.Extensions.Logging` console output (single-line, no color codes) so
  `journalctl -u gitloomd` becomes the one place to look. No custom sink, no file rotation to own.
- **Severity discipline**: startup milestones (DB prepared / migration lock cleared / bound
  `127.0.0.1:5250`), every RPC-handler exception (interceptor-level, with the method name), spawn
  chain steps at Information; noisy per-frame paths (terminal streaming, event fan-out) stay silent
  or Debug-gated.
- **Secrets**: the existing `LoggingMaskTests` mask discipline applies — `// SECRET` fields
  (model keys, tokens) must never reach a log line; extend the mask tests over the new pipeline.
- **A diagnostics affordance**: `HealthCheckStep`/`IDaemonHealthDiagnostics` already read the
  journal tail for OOBE failures — once the daemon actually writes there, that same tail becomes
  useful in the Settings/About surface ("last daemon log lines") for bug reports.

### Acceptance criteria

- `journalctl -u gitloomd` shows startup milestones, the bound endpoint, and every handler
  exception with method + stack — on the stock payload, with no extra configuration.
- The #194 hang scenario, replayed, is diagnosable from the journal alone in under a minute.
- No secret value ever appears in any log line (mask tests extended to the new pipeline).
- Log volume at idle is ~zero (no per-frame chatter).

---

## Sequencing note

Item 2 is small, self-contained, and multiplies the value of every future incident — it should go
first. Item 1 rides the packaging + provisioning seams tier-1/tier-2 just established and closes
the last "fresh install can't actually run an agent" gap; its daemon-side preflight half depends on
nothing and could land independently of the packaging half.

*Related, smaller follow-ups already tracked elsewhere: packaged-build `/p:GitLoomDaemonPayload`
wiring, an App/Server version-lockstep CI guard, a manual-rollback surface for the tier-1
`RestoreRollback` builder, the tier-2 manual-verification runbook (in PR #202's description), a
disk-headroom preflight for the tier-2 upgrade, and the AGENTS.md duplicated-map-block cleanup.*
