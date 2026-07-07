# P2-25 — Cloud Worktrees: Guardrails Now, Implementation Post-GA — Implementation Plan

**Task ID:** P2-25 · **Milestone:** continuous + M8 · **Priority:** guardrails **P0** (they are
CI checks); implementation is **P3-06** (private beta ≤ 2 quarters post-desktop-GA).
**Depends on:** P2-02 protos (guardrails apply from the first proto), P2-14 end-to-end suite
(the WAN payload).
**Branch:** implement on `feature/P2-25-cloud-worktrees-guardrails` off `phase2`; PR targets
`phase2`.

> **Source of truth:** §P2-25 of `docs/GitLoom_Master_Implementation_Document_v2.md`. **This task
> ships guardrails only** — the CI jobs and proto disciplines that keep the cloud door open.
> Actual cloud implementation is specified in P3-06's plan; nothing here builds cloud
> infrastructure.

---

## 0. Context — what exists today

Every daemon feature is being built against a localhost assumption that must never harden into
the protocol. G-14 (transport-agnostic protos) is the design rule; this task makes it
**mechanically enforced**: a WAN-latency CI job that runs the P2-14 end-to-end suite through
netem-degraded networking once per release, plus a proto lint that rejects
localhost/filesystem-path leaks.

### What you can rely on

| Fact | Where |
|---|---|
| All protos in one place | `GitLoom.Protos/protos/gitloom/v1/` |
| P2-14 scripted end-to-end suite (`ScriptedCoordinatorEndToEndTests`) | P2-14 |
| Docker-based CI test wrappers | `docker-compose.yml`, `.github/workflows/ci.yml` |
| Terminal grid protocol + merge-queue RPCs (the two surfaces named in the acceptance) | P2-18 / P2-10 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `build/proto-lint/check-transport-agnostic.sh` (or a small dotnet tool): scans `.proto` files for forbidden patterns — `localhost`, `127.0.0.1`, drive-letter/UNC/absolute-path field comments, non-opaque path fields (naming heuristic `*_path` outside allowlisted opaque-handle messages) |
| **Create** | `.github/workflows/wan-latency.yml` (per-release job: `tc netem delay 80ms` between client and daemon containers → run the P2-14 suite + terminal echo probe) |
| **Create** | `GitLoom.Tests/Wan/WanHarness.cs` (compose file / network setup: daemon container, client test runner container, netem sidecar) + `TerminalEchoLatencyTest.cs` |
| **Create** | `docs/cloud-guardrails.md` (what the guardrails enforce; the P3-06 pointers: mTLS + user auth replacing the session token, per-tenant encryption at rest, `RemoteEnvironment` picker (local VM \| cloud), repo sync via `git push gitloom-cloud` over HTTPS — recorded as future contract, not built) |
| **Edit** | `.github/workflows/ci.yml` — proto lint on every PR touching `GitLoom.Protos/` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

1. Every proto stays **transport-agnostic** (G-14) — enforced by lint on every PR.
2. A **WAN-latency CI job** (`tc netem` 80 ms) runs the P2-14 end-to-end suite **once per
   release**; the grid protocol + merge-queue RPCs must pass it **unchanged**.
3. **Acceptance:** the unchanged P2-14 suite passing over WAN; **terminal echo < 100 ms at 80 ms
   RTT** (i.e., the protocol adds < 20 ms overhead — no chatty round-trips per keystroke).

---

## 3. Implementation steps

1. **Proto lint:** pattern scan + a structural rule: any `string` field whose name matches
   `*path*`/`*dir*` must be inside a message annotated `// opaque-handle` (comment convention
   registered in a checked-in allowlist file, mirroring the P2-04 allowlist discipline — the
   allowlist may only shrink). Failures name file/line. Wire into CI for `GitLoom.Protos/**`
   diffs.
2. **WAN harness:** docker compose — `gitloomd` container, test-runner container, connected via
   a network where a netem rule (`delay 80ms` both directions, applied in a privileged sidecar
   or `cap_add: NET_ADMIN` on the daemon container) degrades traffic. `WanHarness` boots it,
   points the P2-14 suite's `DaemonClient` at the remote address (this forces the suite to
   honor an injectable endpoint — add that seam if the suite hardcodes localhost; that seam is
   itself a guardrail).
3. **Terminal echo probe:** open a terminal session over the degraded link, write a byte, measure
   round-trip to the echoed grid/raw frame; assert p50 < 100 ms at 80 ms RTT (bounded overhead).
   Run 100 iterations, report distribution in the job summary.
4. **Release wiring:** `wan-latency.yml` on release tags + manual dispatch; failure blocks the
   release checklist (document in the workflow header).
5. **`docs/cloud-guardrails.md`:** enforcement description + the P3-06 future contract summary
   so reviewers know *why* a lint failure matters.

---

## 4. Edge-case matrix (binding)

| Case | Required behavior |
|---|---|
| proto adds a `worktree_path` field to a client-facing message | lint fails unless the message is a registered opaque handle |
| suite hardcodes `127.0.0.1` | WAN harness cannot run — the endpoint seam fix is in scope here |
| netem sidecar unavailable (runner without NET_ADMIN) | job fails loudly (infrastructure error ≠ green) |
| flaky WAN timing | echo assertion on p50 of 100 iterations, not single-shot |

---

## 5. Invariants (MUST)

1. Proto lint runs on every PR touching protos; its allowlist only shrinks.
2. The WAN job runs the **unchanged** P2-14 suite — no WAN-specific test forks.
3. Terminal echo p50 < 100 ms at 80 ms RTT.

---

## 6. Test contract

| # | Deliverable | Green criterion |
|---|---|---|
| 1 | proto lint self-test | seeded violation fixtures → exact failures; clean tree passes |
| 2 | WAN job (release cadence) | P2-14 suite green over 80 ms netem |
| 3 | `TerminalEchoLatencyTest` | p50 < 100 ms over the degraded link; distribution in job summary |
| 4 | endpoint-seam unit test | `DaemonClient`/suite accept an injected endpoint |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** WAN-specific forks of the P2-14 suite; lint allowlist growth; skipping the job on
release; opaque-handle annotations slapped on genuinely client-meaningful paths.

```bash
bash build/proto-lint/check-transport-agnostic.sh              # clean
dotnet test --filter "FullyQualifiedName~TerminalEchoLatency"  # via the WAN harness (documented invocation)
grep -rn "localhost\|127.0.0.1" GitLoom.Protos/protos/         # 0 hits
```

---

## 8. Definition of done

- [ ] Proto lint (patterns + opaque-handle rule + shrink-only allowlist) in PR CI.
- [ ] WAN-latency workflow (netem 80 ms) running the unchanged P2-14 suite per release; endpoint seam landed.
- [ ] Terminal echo p50 < 100 ms proven; `docs/cloud-guardrails.md` records the future P3-06 contract.
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-25**, base `phase2`.
