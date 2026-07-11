# GitLoom — Test Implementation Strategy v2 (Phase 2 / Agent Platform)

**Date:** 2026-07-07
**Companion to:** `GitLoom_Master_Implementation_Document_v2.md` — every task `P2-xx` / `P2-Cx` / `P3-xx` there has a section `TI-P2-xx` / `TI-P2-Cx` / `TI-P3-xx` here, grouped by the master doc's categories (§3 build order). A feature PR that does not satisfy its TI section is incomplete by definition.
**Branch:** this document lives on — and all its tests are built on — the **`phase2`** branch, under the same branch policy as the master doc (§0.0 there).
**Relationship to the inline "Required tests" blocks:** Master Doc v2 embeds a summary **Required tests** block in each task. This document is the *expansion* of those blocks into concrete, named test cases — the same relationship v1's `GitLoom_Test_Implementation_Strategy.md` had to Master Doc v1. The inline block is the contract's headline; this document is its case list. If the two ever disagree, the master doc's block wins — and fix the drift here in the same PR.
**v1 strategy still applies:** `GitLoom_Test_Implementation_Strategy.md` §A (conventions, tiers, assertion rules, headless render harness) remains binding for everything it covers. This document only *adds* the infrastructure and specifications Phase 2 needs; it repeats v1 rules only where Phase 2 changes or extends them.

---

## A. Phase-2 conventions, infrastructure, and the definition of "sufficiently tested"

### A.1 What carries over unchanged from v1 §A

- xUnit `[Fact]`/`[Theory]`, naming `Method_ShouldExpectedBehavior_Condition`.
- One test class per feature area, file named after the class.
- Assert repository/system state, not call success; assert exception **types** (+ payload properties), never message substrings.
- Time-based components tested with `Task.WhenAny(tcs.Task, Task.Delay(x))` patterns — no `Thread.Sleep` polling.
- `TempRepoFixture` for anything needing a real repo; no hand-rolled temp-dir plumbing.
- The headless Avalonia harness (TI-00) and the `*RenderHarness` pattern for UI whose layout/theming/interaction is the thing under test.
- Optional-tooling tests are tagged and **skipped, not failed**, when the tool is absent.
- Any test writing outside its fixture directory is a bug.

### A.2 New test tiers (Phase 2 adds four)

| Tier | What it is | Required when |
|---|---|---|
| **Pure unit** (v1) | No IO. Chunkers, parsers, state machines, classifiers, hash chains, planners, bucket math | Always for any pure class — v2 keeps engines pure for exactly this reason |
| **Repo integration** (v1) | `TempRepoFixture` + real repository state assertions | Every mutating Git method (G-6 unchanged) |
| **ViewModel** (v1) | Headless Avalonia + fakes | Every ViewModel behavior named in an invariant |
| **Daemon in-proc** (new) | `GitLoom.Server` hosted via `WebApplicationFactory`, real gRPC channel in-process, `--local-dev` semantics | Every RPC surface: auth, streaming, deadlines, error mapping, interceptor behavior (G-13, G-18, P2-23 enforcement) |
| **Container integration** (new) | Real Docker via the daemon's `SandboxEngine`; tagged `RequiresDocker`, runs on the Linux CI job | Every sandbox/egress/lifecycle invariant that only a real container can prove (G-11, G-15, G-16) |
| **Scripted-swarm** (new) | `ScriptedAgentHarness` (A.4) standing in for a real coding CLI; drives lifecycle/queue/coordinator end-to-ends deterministically | Every orchestration behavior (yield, keep-alive, queue transitions, plan approval, kill switch, repair loop) |
| **WAN/latency** (new) | The P2-14 end-to-end suite re-run under `tc netem` 80 ms (P2-25 guardrail) | Once per release in CI; any proto or grid-protocol change |

### A.3 Trait taxonomy (extends v1's `RequiresGitCli` / `RequiresGitLfs` / `RequiresGpg`)

| Trait | Meaning | Where it runs |
|---|---|---|
| `RequiresDocker` | Needs a Docker daemon (sandbox, egress, reconciler tests) | Linux CI job; skipped locally without Docker |
| `RequiresWsl` | Needs a real WSL2 host (bootstrapper, installer state machines) | Manual matrix + dedicated Windows runner when available; never in the PR gate |
| `RequiresNetwork` | Talks to a real external service (live host smoke, TSA anchor, deploy providers) | Nightly / release pipeline only |
| `LinuxOnly` / `WindowsOnly` | Platform-conditional (forkpty vs ConPTY, DPAPI vs keyring) | Runtime skip on the other OS, both CI legs stay green |
| `Slow` | Pushes the PR suite past its budget | Nightly, never silently dropped (v1 A.5 rule unchanged) |

Rules: a trait-gated test that cannot run **skips with a reason**; a missing precondition never reads as a pass. No test may require **both** WSL and Docker-in-WSL in the PR gate — those combinations live in the manual matrices (P2-05, P2-21, P2-22).

### A.4 TI-P2-00 — Shared Phase-2 test infrastructure (prerequisite; lands with P2-02 at the latest)

New test project **`GitLoom.Server.Tests`** (added to `GitLoom.slnx`, referenced packages: `Microsoft.AspNetCore.Mvc.Testing`, `Grpc.Net.Client`) for the daemon in-proc tier. Pure daemon *logic* (merge-queue state machine, gateway bucket math, hash chain, classifiers) stays in `GitLoom.Core` and is tested from **`GitLoom.Tests`** as ordinary pure units — the split mirrors P2-02's rejection trigger (no business logic in gRPC classes). Record the new project in `AGENTS.md`'s Repository Map in the same PR.

Shared fixtures (all under `GitLoom.Server.Tests/Fixtures/` unless noted):

1. **`DaemonFixture`** — in-proc `GitLoom.Server` host + authenticated `GrpcChannel`; exposes the session token, a wrong-token channel factory, and a log capture sink for the G-13 field-mask assertions. Every daemon in-proc test uses it; hand-rolled hosts are a bug.
2. **`ScriptedAgentHarness`** — a tiny cross-platform console binary (checked into `GitLoom.Tests/TestTools/ScriptedAgent/`) that speaks the P2-09 control protocol (`[IPC_UPDATE_REQUESTED]` → `[IPC_UPDATE_READY]`), executes a scripted timeline (write file → commit → emit output → exit / hang / crash), and can be configured to ignore yield requests (timeout paths). This is the single most important Phase-2 harness: lifecycle, queue, coordinator, curation, checkpoints, repair-loop, and Vibe tests all drive it instead of a real CLI.
3. **`FakeModelEndpoint`** — local HTTP listener scripting model-API responses (200 with rate-limit headers, 401, 429 + `Retry-After`, slow-stream). Used by P2-01 health-check fixtures and the P2-08 "no agent ever sees a raw 429" integration.
4. **`DualRepoFixture`** — a Windows-side `TempRepoFixture` plus an ext4-style bare mirror (plain second directory on CI), wired the way P2-06 provisions: bare clone, `agent/<id>` worktrees, `gitloom-vm` remote. Provides `CaptureRefState()` (v1 TI-19 helper, promoted here) for byte-identical round-trip assertions.
5. **`SandboxFixture`** (`RequiresDocker`) — creates containers through the production `SandboxEngine` only (never raw `docker run`), exposes `InspectAsync()` helpers for the G-11/G-15 assertions and an exec channel for egress probes.
6. **`AuditProbe`** — wraps `IAuditLog` capturing appended events in order; `AssertSequence(params string[] types)` and `AssertExactlyOne(type, predicate)`. Every G-17 touchpoint test asserts through it — one event per operation, by type, never by log-text grep.

**Acceptance for TI-P2-00 itself:** `DaemonFixture` smoke (authenticated echo RPC OK, wrong token `PERMISSION_DENIED`); `ScriptedAgentHarness` self-test (yield round-trip against a bare pipe); `FakeModelEndpoint` replay determinism; suite still headless and green on both CI legs.

### A.5 CI topology

- **PR gate (Windows leg, exists today):** build + full `GitLoom.Tests` + format — the v1 < 3-minute budget still applies; Phase-2 pure/VM/repo tests land here.
- **PR gate (Linux leg, new with P2-02):** build `GitLoom.Server` linux-x64 + `GitLoom.Server.Tests` + `RequiresDocker` suites (the runner has Docker). The P2-06/P2-07 container tests are *PR-blocking* on this leg — sandbox invariants are launch-tier security, not nightly hygiene.
- **Nightly:** `Slow`, `RequiresNetwork`, the VT golden full corpus (P2-04), the 50×-open/close memory harness (P2-13), the 1k events/min SIEM load test (P2-16).
- **Per release:** the WAN-latency job (P2-25) — the unchanged P2-14 suite under `tc netem` 80 ms.
- **Manual matrices** (documented in the PR, never automated away): installer/OOBE/reboot-resume (P2-21), uninstall with a personal distro present (P2-22), terminal feel (P2-03/P2-18 — the v1 A.6 boundary), live OAuth device flows (P2-22), real cloud deploy (P3-04).

### A.6 Definition of "sufficiently tested" for a Phase-2 task

v1's three-part rule carries over: (1) every edge-case-matrix row has exactly one test that fails if the behavior regresses; (2) every in-process-checkable MUST invariant has a test; (3) failure paths asserted by exception type. Phase 2 adds:

4. Every **G-11…G-18** invariant the task touches has a guard test in the same PR (e.g. a task adding a container mount asserts no Windows paths via `SandboxFixture.InspectAsync`; a task adding a proto field with `// SECRET` asserts the log mask).
5. Every **G-17 audit touchpoint** the task introduces is covered by one `AuditProbe` sequence test.
6. **Security-relevant tasks** (P2-01, P2-07, P2-08, P2-11, P2-15, P2-22 per master §0.3.4) paste executed security-check evidence into the PR *in addition to* the automated tests below — the tests are the floor, not the ceiling.
7. Coverage stays a review signal, not a gate (v1 A.5 unchanged).

---

# B. PLATFORM TRACK — M6 foundations (P2-01…P2-08)

---

## TI-P2-01 — BYOK key store + key health check

**Files:** `SecureKeyStoreTests.cs`, `ApiKeyHealthServiceTests.cs`, `CredentialInjectorTests.cs` (pure/process); `ApiKeySettingsViewModelTests.cs` (ViewModel).

1. `Set_Get_Delete_ShouldRoundTrip_ThroughISecureKeyStore` — `llm_anthropic` via the new interface → retrievable, then `Delete` → keyring file gone (assert on disk).
2. `CheckAsync_Anthropic_ShouldParseRateLimitHeaders` — recorded 200 fixture with `anthropic-ratelimit-*` headers → `RequestsPerMinute`/`TokensPerMinute` populated, `EstimatedConcurrentAgents` matches the documented table (`[Theory]` over table rows).
3. `CheckAsync_OpenAi_ShouldUseModelsEndpoint_AndBearerHeader` — assert the outgoing request shape via the `HttpMessageHandler` seam.
4. `CheckAsync_401_ShouldReturnInvalid_WithKeyScrubbedFromReason` — the reason contains the provider message and **never** any substring of the key.
5. `CheckAsync_MissingHeaders_ShouldReturnValid_WithNullCeilings_AndAgentsFloorOne`.
6. `CheckAsync_Unreachable_ShouldThrowTyped_AndStoreNothing`.
7. `BuildEnvFileContent_ShouldEmitNewlineTerminatedPairs_InMemoryOnly` (pure; no file IO in the implementation — assert by API shape).
8. `BuildEnvFileContent_ShouldThrowArgumentException_OnNewlineInValue`.
9. VM: `Save_InvalidKey_ShouldNotPersist` — fake health returns invalid → keyring directory empty, inline error set.
10. VM: `Save_ValidKey_ShouldRecheckHealth_AndNullLocalCopies` — re-save overwrites atomically, health line updates.
11. `TosAcknowledgment_ShouldPersistAcrossContextReopen` — record → new `AppDbContext` → queryable with provider + timestamp.
12. Guard: `grep -rn "llm_" GitLoom.App/ | grep -i "preferences\|settings.json"` → 0 hits (reviewer script; not automated as a source-grep test — v1 TI-16 rule).

---

## TI-P2-02 — Daemon + gRPC v1 contract

**Files:** `GitLoom.Server.Tests/DaemonAuthTests.cs`, `TerminalStreamRpcTests.cs`, `DaemonClientReconnectTests.cs`, `LoggingMaskTests.cs` (daemon in-proc via `DaemonFixture`).

1. `AuthenticatedCall_ShouldSucceed`; `WrongToken_ShouldReturnPermissionDenied`; `MissingToken_ShouldReturnPermissionDenied` — and no method is exempt: `[Theory]` over **every** service/method pair reflected from the proto descriptor set (new RPCs are covered automatically; an allowlist appearing later fails this test).
2. `Daemon_ShouldBindLoopbackOnly` — assert the listening endpoint address.
3. `TerminalAttach_BidiEcho_ShouldRoundTripBytes` — write frames, read identical frames back through the stub streamer.
4. `StreamAgentEvents_ShouldResume_AfterDaemonRestart` — kill and restart the in-proc host; `DaemonClient` reconnects with backoff and the stream resumes (TCS-gated, no sleeps).
5. `DaemonClient_ConnectionState_ShouldTransition_ConnectedDegradedDown` — observable sequence asserted across a forced drop.
6. `PortAlreadyBound_ShouldFailTypedNamingPort` — pre-bind the port, start → typed startup failure.
7. `TokenFileDeletedWhileRunning_ShouldNotBreakExistingChannels`.
8. `SecretMaskedField_ShouldNeverAppearInLogs` — invoke an RPC carrying a `// SECRET` field with a sentinel value → log capture sink contains zero occurrences (the G-13 field-mask test the master doc names).
9. `RpcWithoutDeadline_ShouldBeImpossible` — client wrapper test: every `DaemonClient` call site sets a deadline/cancellation (assert via the wrapper API shape; a raw stub call in App code is a review rejection, not a test).
10. `UnimplementedStubs_ShouldReturnTypedUnimplemented` — `RepoSyncService`/`GatewayService` stubs before P2-06/P2-08.

---

## TI-P2-03 — Terminal engine (interim PTY + vendored renderer)

**Files:** `VtBoundaryDetectorTests.cs` (pure), `PtySessionTests.cs` (integration, `LinuxOnly`+`WindowsOnly` variants), `TerminalStreamerTests.cs`, `TerminalViewModelTests.cs` (ViewModel).

1. `SafeFlushLength_ShouldHoldTail_WhenSplitAtEveryOffset` — `[Theory]` over the corpus (CSI SGR, OSC 8 with both terminators, DCS, SS3, 2/3/4-byte UTF-8, ZWJ emoji): for **every byte offset** of every sequence, `prefix + heldTail` reassembles byte-identically (the master doc's "exhaustively tested" invariant, literally).
2. `SafeFlushLength_MalformedEndlessEscape_ShouldFlushAt4KbCap`.
3. `PtySession_Isatty_ShouldBeTrue` — spawn a probe process asserting `isatty(1)`.
4. `PtySession_Spawn_CatEcho_ShouldRoundTripBytes`; `Kill_ShouldCompleteExitCode`; `Resize_ShouldPropagateToWinsize` (probe reads `TIOCGWINSZ`/console size).
5. `TerminalStreamer_ShouldNeverSplitSequenceAcrossFrames` — feed a scripted byte stream with sequences straddling the 16 ms tick; every emitted frame ends on a detector boundary.
6. `TerminalStreamer_100MbFlood_ShouldKeepMemoryFlat` — `yes`-style generator; assert pooled-buffer bytes and scrollback stay under a fixed ceiling (`Slow`).
7. VM: `CtrlC_ShouldSend0x03ToInputStream`; `Resize_ShouldPropagate`; `Scrollback_ShouldCapAt10kLines_Circular`.
8. Render harness: `TerminalRenderHarness` PNG of a colored TUI frame in two themes (v1 A.6 pattern); interaction feel remains manual (v1 boundary).

---

## TI-P2-04 — VT conformance & replay harness

The harness **is** the deliverable (master doc). Tests about the harness itself:

1. `Goldens_Regenerate_ShouldBeByteIdentical` — regenerate every golden transcript locally, diff against committed goldens → identical (determinism invariant).
2. `Replay_ShouldCompareCellByCell_NotTiming` — inject an artificial delay into the feed; grid comparison result unchanged (timing-dependence guard).
3. `KnownFailuresAllowlist_ShouldOnlyShrink` — CI diff check: the checked-in allowlist file may lose lines, never gain them (implemented as a CI script step, referenced here as contract).
4. Coverage-matrix presence: one golden per required behavior — alternate screen, DEC 2026, truecolor, CJK/emoji width, bracketed paste, mouse reporting, OSC 8 — a `[Theory]` enumerates the matrix and fails if a golden file is missing.
5. `Harness_ShouldDriveBothEngines_ThroughGridReadback` — the same fixture run against the interim engine and (later) libvterm produces comparable grids via the shared abstraction; until P2-18 this asserts the abstraction seam exists (interim engine only).

---

## TI-P2-05 — GitLoomOS bootstrapper

**Files:** `WslConfigMergerTests.cs`, `BootstrapStateMachineTests.cs` (pure; check/act seams mocked). Real-WSL behavior is the manual matrix (`RequiresWsl`).

1. `Merge_ShouldPreserveExistingUserKeys_AndAddOnlyOurs` — `[Theory]` over fixture `.wslconfig` files: user `[wsl2]` keys survive, our keys added, unrelated sections untouched, backup written first.
2. `Merge_ShouldBeIdempotent` — merging twice equals merging once.
3. `Merge_MemoryDefault_ShouldBeMinHalfRamOr8Gb` — table over RAM sizes.
4. `StateMachine_EveryStep_ShouldCheckThenAct` — with all checks green, zero acts invoked (full no-op re-run invariant).
5. `StateMachine_PartialBootstrap_ShouldResumeAtFirstFailedStep` — fail step N, re-run → steps 1..N-1 skipped, N retried.
6. `StateMachine_WslNotInstalled_ShouldFailActionable_BeforeAnyAct`.
7. `Lifecycle_ShouldNeverEmitShutdown` — the command builder never produces `wsl --shutdown` for any state (G-12; asserted on the built command list, plus the reviewer grep).
8. Manual matrix in the PR: fresh import < 60 s; `docker info` green in the VM; `wsl --terminate` mid-bootstrap then resume; other distros untouched.

---

## TI-P2-06 — Repo provisioner + quarantine remotes

**File:** `RepoProvisionerTests.cs`, `AgentWorktreeManagerTests.cs` (integration on `DualRepoFixture`; Linux CI leg).

1. `Provision_FirstRun_ShouldCreateBareMirror_AtSha256Path` — hash of the normalized path matches; bare repo exists; `core.untrackedCache` set from the template.
2. `Provision_SecondRun_ShouldFetchIncrementally_NotReclone` — object count/mtime evidence that no re-clone happened (the "test measures" row).
3. `Provision_PathWithSpacesAndUnicode_ShouldHashAndRegisterCorrectly` — including the `gitloom-vm` remote URL registration idempotence (run twice → one remote).
4. `Provision_BareRepoManuallyDeleted_ShouldRecloneCleanly`.
5. `CreateAgentWorktree_ShouldBranchFromMain_AtExpectedPath`; `CreateAgentWorktree_DuplicateAgentId_ShouldThrowTyped`.
6. `RemoveAgentWorktree_Dirty_ShouldThrowWithoutForce_AndSucceedWithForce`; `Prune_ShouldCleanStaleMetadata`.
7. `CommitRoundTrip_ShouldBeByteIdentical_WindowsToVmAndBack` — commit in the agent worktree → `git fetch gitloom-vm && git merge agent/<id>` on the Windows fixture → tree SHA and blob bytes equal (`CaptureRefState` before/after).
8. `PnpmInstall_ShouldRunPostWorktree_OnlyWhenLockfilePresent` (command-issued assertion via the runner seam; the real install is not exercised in CI).
9. **Quarantine remotes:** `AgentWorktree_ConfiguredRemotes_ShouldBeDaemonBareRepoOnly` — enumerate remotes inside the worktree → exactly one, pointing at the bare mirror; no URL resembling the user's real remote, no credential material present (the "structurally impossible force-push" invariant).
10. `AgentPush_ShouldLandInBareRepo_NeverUpstream` — scripted push from the worktree → bare repo ref moved; the fixture's fake "real remote" untouched.

---

## TI-P2-07 — Sandbox hardening + default-deny egress

**Files:** `ContainerSpecBuilderTests.cs` (pure), `SandboxEgressTests.cs`, `SandboxInspectTests.cs` (`RequiresDocker`, Linux CI leg — PR-blocking per A.5).

1. `BuildCreateRequest_ShouldSetAllHardeningFlags` — pure assertion on the create request: `no-new-privileges`, userns remap, seccomp default, memory+pids limits, read-only rootfs where configured, tmpfs `/dev/shm`.
2. `BuildCreateRequest_MountSources_ShouldBeExt4WorktreeOnly` — any `/mnt/c`, `drvfs`, or UNC source in the requested mounts → the builder throws typed (G-11 enforced at construction, not just inspected after).
3. `Inspect_RunningAgentContainer_ShouldShowNoWindowsPaths_UsernsAndLimits` — `SandboxFixture` + `docker inspect` (the G-11/G-15 runtime proof the master doc names).
4. Egress matrix (each its own test, all `RequiresDocker`): `Curl_AllowlistedModelApi_ShouldSucceedViaProxy`; `Curl_NonAllowlistedDomain_ShouldFailFast_RefusedNotTimeout` (assert elapsed < threshold); `DirectIpEgress_ShouldBeDropped_DespiteProxyEnvUnset` (the iptables backstop — proxy-env-only enforcement is the named rejection trigger); `DnsExfil_ShouldFail` (`dig x.attacker.tld` → NXDOMAIN/refused via pinned DNS).
5. `DevboxAdd_DuringLivePty_ShouldSurviveSession` — exec `devbox add jq` while a `ScriptedAgentHarness` PTY is attached → session unbroken, tool available.
6. `CredentialTmpfs_ShouldBePerAgent_Mode0400_AndAbsentFromImage` — two agents → distinct tmpfs contents; `stat` mode 0400; no `~/.claude`/global auth-dir mounts anywhere in the spec.
7. `NoRuntimeImageBuild_ShouldHoldByConstruction` — the engine exposes no build path; reviewer grep `ImageBuild` (G-16) recorded in the PR script.
8. Allowlist edits: `EgressAllowlistChange_ShouldEmitAuditEvent` (`AuditProbe`; feeds P2-17).
9. sbx backend (when/if added): the same spec-builder + egress matrix runs against the sbx implementation of `SandboxEngine` (`[Theory]` over backends) — the invariants apply to both, per the master doc's MAY clause.

---

## TI-P2-08 — AI Gateway + admission control + swarm reconciler

**Files:** `TokenBucketTests.cs`, `BudgetTests.cs`, `BackoffTests.cs` (pure, property-style), `AiGatewayIntegrationTests.cs` (scripted-swarm + `FakeModelEndpoint`), `AdmissionControllerTests.cs`, `SwarmReconcilerTests.cs` (`RequiresDocker`).

1. Bucket properties (`[Theory]`/property-style): refill never exceeds capacity; burst drains then throttles; two competing agents under sustained load each make progress within a bounded window (fairness — neither starves); FIFO within a priority class.
2. `AcquireAsync_ShouldRespectEstimatedTokens_AndReleaseActuals`.
3. `Report429_WithRetryAfter_ShouldPauseWorker_AndResumeAfterDelay` — scripted agent + `FakeModelEndpoint` scripting one 429 `Retry-After: 5`: worker input paused, state `RateLimited`, resumes ≈5 s (virtual clock), and the CLI-side transcript shows **only** a delayed 200 — the "no agent process ever observes a raw 429" invariant, asserted end-to-end.
4. `BudgetExhaustedMidTask_ShouldPauseWithTypedReason_NotKill` — container still exists, state reflects the reason, `AuditProbe` shows `budget_exceeded`.
5. `GetSnapshot_ShouldReportPerAgentSpend_AndQueueDepth`; spend stream over `GatewayService` (daemon in-proc).
6. `CanSpawn_ShouldRejectAboveMemoryThreshold_WithTypedReason` — fake `/proc/meminfo` sampler at 86% → rejected, existing agents untouched; message contains the honest ceiling text.
7. `Reconciler_DeadContainer_ShouldPruneWorktreeAndMarkDead`; `Reconciler_OrphanLiveContainer_ShouldAdoptOrStopPerPolicy`; `Reconciler_OutOfBandDockerRm_ShouldConvergeOnBoot` (`RequiresDocker`) — Docker is the sole source of truth: the suite contains **zero** lockfile/PID-file reads (rejection trigger).
8. `Reconciler_ShouldTrustDockerOnly` — delete any on-disk state the daemon keeps, reboot reconcile → outcome identical.

---

# C. PLATFORM TRACK — M7 the verified swarm (P2-09…P2-14, P2-18, P2-21, P2-22)

---

## TI-P2-09 — Agent lifecycle: cooperative yield + keep-alive rebase

**File:** `AgentLifecycleTests.cs` (scripted-swarm on `DualRepoFixture`; `RequiresDocker` for the pause/leader cases).

1. `Yield_ShouldRoundTrip_RequestedThenReady` — scripted agent acknowledges; only then does the daemon touch the worktree (ordering asserted via the harness timeline).
2. `Yield_Timeout_ShouldDockerPause_ThenProceed` — harness configured to ignore the request → `docker pause` observed before any Git mutation.
3. `KeepAlive_ShouldCommitWipAndRebaseOntoMain` — human commit on main → cycle → agent branch reparented, wip commit present.
4. `KeepAlive_AgentMidOwnRebase_ShouldSkipAndRetryNextCycle` — arrange a rebase-in-progress marker in the worktree → guard skips, no mutation, next cycle succeeds.
5. `KeepAlive_Conflict_ShouldSetStatusConflict_AndRouteToResolver` — induced conflict → status `Conflict`, T-04 resolver payload targets the worktree.
6. `GitMutation_IndexLock_ShouldRetryWithBackoff` — hold `.git/index.lock` briefly → operation succeeds after release, bounded retries.
7. `SessionLeader_ShouldSurviveDaemonKill9_AndReattach` — kill the daemon process, restart → PTY reattaches, scripted session uncorrupted.
8. `Teardown_ShouldLeaveNoResidue` — dispose the agent context → `git worktree list` clean, branch gone, `docker ps -a` clean, no floating dock windows (VM-side assertion).
9. `HumanEdits_ShouldReachWorktreeOnlyViaGit` — modify a file on the Windows side without committing → after a keep-alive cycle the agent worktree does **not** contain it (no file sync); after committing it does (via rebase).

---

## TI-P2-10 — Merge queue + verification runs + stale invalidation

**Files:** `MergeQueueStateMachineTests.cs` (pure — the densest suite of the milestone, per the master doc), `MergeQueueIntegrationTests.cs` (scripted-swarm), `ForegroundMergeServiceTests.cs` (repo integration).

State machine (pure; transitions table-driven):
1. `Transition_Table_ShouldMatchSpec` — `[Theory]` over every legal transition of `WorkerMergeState`; every illegal transition throws typed.
2. `NotifyMainMoved_ShouldFlipAllVerifiedToStaleVerified_AndRequeue` — 3 workers `Verified` → all `StaleVerified`, re-entry order preserved.
3. `CanMerge_ShouldBeFalse_OnStaleOrUnverified_WithReason`; `CanMerge_Override_ShouldBeAllowed_ButJournaledAndAudited` (`AuditProbe`: `stale_override_used`).
4. `VerificationRecord_ShouldBeImmutable_AndKeyedToMainSha` — re-verification creates a *new* record; the old one is untouched.
5. `NoTestCommandConfigured_ShouldReturnTypedReason_AndAllowOnlyExplicitOverride`.
6. `StatePersistence_ShouldSurviveDaemonRestart` — serialize mid-`Verifying`, reload → run restarts or resumes, never stuck (integration variant below proves it live).

Integration (scripted-swarm):
7. `TwoWorkers_AMergesFirst_BReverifiesBeforeMergeEnabled` — the master doc's canonical two-worker scenario: A merges → B `StaleVerified` → auto rebase+re-verify → only then `CanMerge(B)`.
8. `VerificationFailsAfterRebase_ShouldReturnToWorking_WithFailureSurfaced_NotSilentlyRetried`.
9. `Verification_ShouldRunInWorkerSandbox_NeverHost` — the scripted test command writes a marker file; assert it exists in the container filesystem and not on the host (rejection-trigger guard).
10. `ForegroundMerge_ShouldFetchAndMergeOnWindowsRepo_Journaled` — merge lands via T-19 journal (undo restores pre-merge `CaptureRefState`).
11. `PostMergeInstall_ShouldUseIgnoreScripts_PoisonedPostinstallDoesNotExecute` — the canary: a fixture package with a `postinstall` writing a sentinel → sentinel absent after the wrapped install (retry wrapper exercised with one injected `EBUSY`).
12. `NoAutoMergePathExists` — API-shape test: nothing in `IMergeQueue` or the daemon surface can move a branch to `Merged` without the human foreground call.

---

## TI-P2-11 — Review cockpit: risk rank, provenance, flagged gate

**Files:** `RiskClassifierTests.cs`, `ProvenanceReaderTests.cs`, `FlaggedChangeDetectorTests.cs`, `LockfileSemanticDiffTests.cs` (all pure, fixture corpora); `ReviewCockpitViewModelTests.cs` (ViewModel); one end-to-end extending the P2-10 canary.

1. `Classify_Corpus_ShouldMapEveryCategory` — `[Theory]` over fixtures for each `RiskCategory`, including the load-bearing distinction: `PackageJson_DependencyBumpOnly_ShouldBeLockfile_NotExecutableConfig` and `PackageJson_ScriptAdded_ShouldBeExecutableConfig`.
2. `Classify_RenamedFileWithRiskyContent_ShouldUseNewPathPlusContent`.
3. `FromTrailers_Matrix` — `Agent:`/`Task:`/`Plan:` present/partial/absent/malformed → correct `HunkProvenance` or null, never a throw.
4. `FromAgentTrace_ShouldMapFileLineRangesToContributors` — fixture trace JSON (including one produced by our own orchestrator emitter and one hand-built "external vendor" shape) → hunk mapping; malformed JSON → typed, not crash.
5. `HumanCommitWithoutProvenance_ShouldRankButShowNoChip` (VM).
6. `Detect_FlaggedChanges_ShouldRequirePerItemAcknowledgment` (VM) — acknowledgment is per item; a single global toggle satisfying the gate must be impossible by construction (rejection trigger).
7. `Acknowledgments_ShouldBindToDiffHash_AndResetOnNewPush` — new commit on the branch → previously acknowledged items revert to unacknowledged.
8. `RiskOrdering_ShouldReorderNeverHide` — hunk count identical pre/post ordering (invariant 3).
9. Semantic lockfile diff: `LockfileDelta_ShouldListAddedUpdatedPackages_MaintainerChange_AndInstallScripts` per ecosystem fixture (npm/pnpm/csproj/poetry); `OsvCheck_ShouldFlagKnownCve_FromLocalDb` (offline database fixture).
10. `BringBranchLocal_ShouldFetchIntoLocalWorktree_ViaT29Plumbing` (repo integration) — the pairing-style action round-trip.
11. End-to-end (extends the P2-10 canary): poisoned-postinstall branch → `ExecutableConfig` panel appears → merge blocked → acknowledge item-by-item → `CanMerge` true.
12. `ReviewSprintMode_DeferredHunks_ShouldRecordAsUnviewed` — feeds the P2-38 coverage map (VM; full receipts spec in TI-P2-38).

---

## TI-P2-12 — External agent PR intake

**File:** `ExternalPrIntakeTests.cs` (fixture-driven against the T-23 provider seam; no live network in the PR gate).

1. `PollOnce_NewMatchingPr_ShouldMaterializeQueueEntry` — fixture PR list with author filter `codex[bot]` → fetch `pull/<n>/head` into the bare repo as `agent/pr-<n>`, worktree created, queue state `Working`.
2. `PollOnce_SamePrTwice_ShouldBeIdempotent` — one entry, no duplicate worktrees.
3. `PrForcePushed_ShouldInvalidateVerification_AndRequeue` — old `VerificationRecord` no longer satisfies `CanMerge`.
4. `PrClosedUpstream_ShouldCancelEntry_AndPruneWorktree`.
5. `PollRateLimited_ShouldBackoff_ThroughTypedHostError_NeverCrashLoop` — fixture returns the typed rate-limit error → bounded backoff, poller alive.
6. `MergePathDispatch_ShouldUseHostApiForPrEntries_AndLocalForegroundForLocalAgents` — the pluggable merge step unit test the master doc names.
7. `Intake_ShouldWriteNothingUpstream_WithoutExplicitUserAction` — full poll+verify cycle → the fake host transport records zero mutating calls.
8. `AuthorFilter_ShouldBeConfigurable_AndMatchBotAccounts` (`[Theory]`).

---

## TI-P2-13 — Activity bar & docking UI

**Files:** `AgentStatusBrushConverterTests.cs` (pure), `ActivityBarViewModelTests.cs` (ViewModel), `ActivityBarRenderHarness.cs` (headless render), `DockLifetimeMemoryTests.cs` (`Slow`, nightly).

1. `StatusToBrush_ShouldMapEveryAgentStatus_ThroughDesignTokens` — one converter, every status, no raw colors (assert resource-key lookup, not color values).
2. `AgentList_ShouldOrderLifo_AndVirtualize`.
3. `IsAttentionRequired_ShouldDeriveFromWaitingBlockedTransitions`; `OsNotification_ShouldSuppress_WhenForegroundedOnThatAgent` (fake notifier records).
4. `ResourceMonitor_ShouldRenderGatewaySnapshotStream` (fake `GatewayService` feed → sparkline VM state).
5. `OpenCloseAgentTab_50x_ShouldKeepHeapStable_AndZeroFloatingWindows` — the blocking memory test: force GC between iterations, assert bounded heap growth and `Application.Current` window count (the documented Dock.Avalonia leak guard).
6. Render harness: PNG of the bar with 4 fake agents in **every one of the five themes** (the master doc says all themes — Daylight Loom included).
7. `LayoutPersistence_ShouldRoundTripDockState`.

---

## TI-P2-14 — Plan approval + dual-mode orchestration

**Files:** `TaskPlanSchemaTests.cs` (pure), `CoordinatorToolsTests.cs`, `OrchestrationEndToEndTests.cs` (scripted-swarm), `TerminalLockTests.cs` (daemon in-proc), `KillSwitchTests.cs`.

1. `TaskPlan_SchemaValidation_Corpus` — `[Theory]`: valid plan; missing `Scope`; wrong types; extra fields; oversized → typed validation results.
2. `PlanRejected_ShouldNeverSpawnWorker_AndLeaveNoWorktreeResidue` — filesystem + docker state clean after rejection.
3. `PlanApproved_ShouldPersistPlanAndApproverIdentity` — queryable record (P2-15 will chain it; `AuditProbe` sees `plan_approved`).
4. `SpawnWorker_ShouldRespectAdmissionAndBudgetCaps` — coordinator tool call above the cap → typed rejection surfaced to the coordinator, no container.
5. `ManualModeSpawn_ShouldBypassCoordinator_ButNotAdmissionOrBudgets`.
6. `TerminalLock_ManagedWorker_ShouldSeverInputAtGrpcLayer` — a **hand-crafted gRPC client** (raw stub, not `DaemonClient`) attempts input on a locked terminal → rejected server-side (the master doc's explicit "not just UI read-only" invariant).
7. `Coordinator_ShouldBeUnableToInvokeMergeRpcs` — interceptor-enforced role: coordinator credentials calling any merge RPC → `PERMISSION_DENIED` (convention is not enforcement).
8. `KillSwitch_ShouldYieldAll_PauseOnTimeout_FreezeQueue_AndSnapshotJournal` — 3 scripted agents, one ignoring yield → all containers paused < 5 s (virtualized where possible; measured in the integration leg), queue frozen, `AuditProbe` shows `killswitch`.
9. End-to-end: `TwoIndependentTasks_ShouldRunParallel_VerifySequentiallyMergeWithStaleReverify` — the master doc's canonical scripted-coordinator scenario, asserting the full event sequence through `AuditProbe`.

---

## TI-P2-18 — Terminal target engine (libvterm + Skia grid)

**Files:** the **entire TI-P2-04 suite re-run against libvterm is the merge gate** (no golden regression, allowlist ≤ interim's); plus `VtermInteropTests.cs` (`LinuxOnly`), `GridSnapshotTests.cs`, `DamageCoalescingTests.cs`.

1. `P2-04 conformance + replay on TerminalEngine=libvterm` — parity or better; any golden regression blocks the engine flag flip.
2. `VtermSession_SbPushPopLine_ShouldMaintainScrollback` (interop, `LinuxOnly`).
3. `Snapshot_Attach_ShouldRenderIdenticalGrid` — kill the client mid-`htop`-golden replay, reattach from snapshot → cell-by-cell identical grid (the named invariant).
4. `DaemonRestart_LeaderAlive_ShouldLiveReattach`.
5. `Sustained50MbCat_ShouldSendNoFullGridInSteadyScroll` — assert frame stream contains scroll ops + damage rects only; client CPU proxy = bytes-per-frame ceiling; the perf measurement itself is pasted in the PR (master doc).
6. `GridUpdate_CellRuns_ShouldCarryCombiningTruecolorAndWidth` — CJK double-width + ZWJ emoji fixtures through the proto round-trip.
7. `EngineFlag_ShouldSwapWithoutViewModelChanges` — both engines behind `ITerminalView`; the ViewModel test suite runs unchanged against each (the P2-03 invariant paying off).
8. Manual matrix (PR evidence): Claude Code, vim, htop, tmux sessions on the new engine.

---

## TI-P2-21 — Installer part 1: diagnostics, OS enablement, payload

**Files:** `SystemDiagnosticsParserTests.cs`, `OobeStateMachineTests.cs` (pure); tarball build + upgrade in CI; the rest is the manual matrix (`RequiresWsl`).

1. `WslStatusParser_ShouldParseCapturedOutputs_PerWslVersion` — `[Theory]` over checked-in captured `wsl --status`/`--list` outputs across WSL versions/locales.
2. `Diagnostics_EachCheck_ShouldReturnPassOrActionableFail` — build check, virtualization flags (fixture WMI data), disk space, ARM64 → explicit unsupported gate; every `Fail` carries a message + doc link (assert non-empty, typed).
3. `Diagnostics_AnyHardFail_ShouldStopBeforeAnySystemModification` — state machine ordering test.
4. `OobeStateMachine_RebootResume_ShouldContinueFromPersistedState` — `oobe-state.json` round-trip; resume task is the elevated Scheduled Task path (command-builder assertion: never `RunOnce`).
5. `TarballBuild_ShouldBeHashStable_GivenPinnedInputs` — CI job builds `GitLoomOS.tar.gz` twice → identical hash; `/etc/gitloomos-release` version stamped.
6. `VmUpgrade_VnToVnPlus1_ShouldPreserveReposAndWorktrees` — automated against a fixture VM image where CI allows; otherwise the scripted upgrade harness on directory-level state + the manual matrix.
7. Manual matrix (PR evidence): fresh install end-to-end, UAC-at-Construct-Sandbox only, reboot-resume, VM snapshot upgrade.

---

## TI-P2-22 — Installer part 2: Windows integration, OAuth, adapter channel, teardown

**Files:** `LoopbackOAuthListenerTests.cs`, `PkceTests.cs` (pure/process), `AdapterManifestTests.cs`, `DeepLinkTests.cs`; uninstall is a manual matrix.

1. `Pkce_VerifierChallenge_ShouldMatchRfc7636Vectors`.
2. `Listener_ShouldRejectMismatchedState`; `Listener_ShouldBeSingleUse`; `Listener_ShouldTimeoutAtFiveMinutes` (virtual clock); `Listener_ShouldBindEphemeralLoopbackPort`.
3. `DeepLink_GitLoomScheme_ShouldCarryNoSecrets` — code-path test: the deep-link builder API accepts no token-typed inputs; plus the reviewer grep for tokens in `gitloom://` construction sites.
4. `AdapterManifest_SchemaValidation_Corpus` — valid, missing health probe, unpinned version (`@latest` → **rejected by schema**), unknown fields.
5. `AdapterPin_ShouldSurviveBreakingUpstreamRelease` — simulate the channel serving a newer breaking version while the pin names the old one → installed adapter version unchanged (the simulated test the master doc names).
6. `AdapterInstall_ShouldRunInsideVm_AtPinnedVersion_WithHealthProbe` (integration on the Linux leg with a fixture channel server).
7. `ContextMenus_InstallWritten_UninstallRemoved` — registry fixture round-trip (WindowsOnly).
8. Manual matrix (PR evidence): uninstall on a machine with a personal distro — `--terminate` → poll → `--unregister` only ours; registry/tasks/appdata removed; user repo + optional `gitloom-vm` remote handling; G-12 grep.

---

# D. PLATFORM TRACK — M7.5 trust (P2-15…P2-17, P2-19, P2-20)

---

## TI-P2-15 — Tamper-evident audit log

**Files:** `HashChainTests.cs` (pure, property-style), `AuditLogTests.cs` (daemon SQLite integration), `AuditTouchpointCoverageTests.cs` (scripted-swarm), `Rfc3161AnchorTests.cs` (`RequiresNetwork`).

1. `ComputeHash_ShouldBeDeterministic_OverCanonicalJson` — sorted keys, invariant culture; the same payload object always hashes identically (property test over generated payloads).
2. `Verify_TamperSweep_ShouldFailAtExactSeq` — for a 100-record chain, flip one byte in every position (payload, prevHash, timestamp) → `Verify` reports `FirstBadSeq` == the tampered record, every time.
3. `Append_ShouldChainFromPreviousHead`; `Append_CrashMidWrite_ShouldLeaveNoTornRecord` — kill the writer between SQLite and file-mirror writes → reopen verifies clean and resumes (transactional invariant).
4. `Redact_ShouldReplacePayload_KeepChainValid_AndReferenceOriginalHash` — post-redaction `VerifyAll` passes; the redaction event carries the original hash; the original payload is unrecoverable from disk.
5. `NoRewritePathExists` — API-shape: `IAuditLog` exposes no update/delete; the SQLite table is append-only (trigger/constraint asserted).
6. `EncryptionAtRest_ShouldLeaveNoPlaintextPromptOnDisk` — append an `inference` event with a sentinel prompt → raw file/DB bytes contain no sentinel.
7. `TouchpointCoverage_ScriptedSwarmSession_ShouldEmitExpectedEventSequence` — spawn → plan approve → verify → stale override → merge → kill switch, asserted via `AuditProbe` as an exact ordered sequence with **exactly one** event per operation (G-17 idempotence).
8. `TsaUnreachable_ShouldQueueAnchor_AndKeepAppending` — anchoring is best-effort, chaining is not (fake TSA endpoint down).
9. `AnchorRoundTrip_ShouldValidateAgainstRealTsa` (`RequiresNetwork`, nightly).
10. `AuditReplay_ShouldRederiveFullChainForCommit` — merge-decision replay: given a fixture main commit, `audit replay <sha>` stitches plan → agent → verification → receipts, chain intact (extension; grows as P2-38/42/43 land).

---

## TI-P2-16 — SIEM exporter

**File:** `SiemExporterTests.cs` (integration: local syslog container + mock HEC, `RequiresDocker`), `SiemSchemaTests.cs` (pure).

1. `Export_CefOverSyslogTls_ShouldDeliverEveryEvent` — local syslog container round-trip.
2. `Export_SplunkHec_ShouldDeliver_AndHonorAck` (mock HEC).
3. `SinkOutage_ShouldBufferAndRedeliver_ZeroLossUpToCap` — kill the sink, emit N < cap, restore → all N delivered in order.
4. `BufferPastCap_ShouldEnterLoudState_AndReportDrops` — the "loud past the cap" invariant: delivery-status panel state + counted drops, never silent.
5. `JsonEvents_ShouldValidateAgainstSchema_Corpus` — every P2-15 event type through the JSON-schema validator (schema checked in next to `docs/siem-events.md`).
6. `LoadTest_1kEventsPerMinute_ShouldSustain` (`Slow`, nightly).

---

## TI-P2-17 — Source-available split + network transparency

**Files:** CI license check (script, contract recorded here), `NetworkTransparencyViewModelTests.cs` (ViewModel over fixture proxy logs), `ProxyLogStreamTests.cs` (integration).

1. `LicenseHeaders_ShouldMatchArtifactBoundary` — CI check: every file in the FSL-published projects carries the FSL header; private projects carry none/proprietary (script asserted in CI, referenced as contract).
2. `TransparencyView_ShouldStreamAllowedAndDeniedConnections` — fixture proxy-log stream containing one allowed model call + one denied attempt → both rows appear with destination/agent/bytes/verdict within the streaming window (TCS-gated).
3. `TransparencyView_Filter_ShouldNarrowByAgentAndVerdict`; `Export_ShouldWriteCompleteLog`.
4. `LiveIntegration_AllowedAndDeniedEgress_ShouldAppearWithinSeconds` (`RequiresDocker`; piggybacks the P2-07 egress matrix containers).
5. Doc-claims checklist: every claim in `docs/security-architecture.md` maps to a named test or config reference — a review checklist pasted in the PR, per the master doc (not automated).

---

## TI-P2-19 — Cross-worktree conflict radar

**File:** `ConflictRadarTests.cs` (integration on a fixture bare repo), `RadarClassificationTests.cs` (pure — the classification logic is separated from git plumbing per the MUST).

1. `Scan_ThreeBranchFixture_ShouldReturnExactWarningSet` — the master doc's canonical fixture: branch pair with a same-line edit → `CertainConflict=true`; same-file-disjoint pair → soft warning; no-overlap pair → absent.
2. `Scan_BinaryFileOverlap_ShouldWarnFileLevelOnly_NeverChunk`.
3. `Scan_BranchIdenticalToMain_ShouldProduceNoSelfNoise`.
4. `Scan_ShouldPrefilterByNameOnlyDiff_BeforeChunking` — instrument the chunker seam: files outside the name-only intersection are never chunked (the rejection-trigger guard, and the 15-pair boundedness evidence for the PR).
5. `Warning_ShouldClear_AfterRebaseRemovesOverlap`.
6. `Scan_ShouldReadBareRepoObjectsOnly_NeverWorktreesOrIndex` — no worktree file handles opened, no `index.lock` created during a scan (watch the fixture worktree directory).
7. `NewOverlap_Event_ShouldFireOnceOnNewFindingsOnly` — repeat scan with unchanged state → no event.
8. Symbol-level extension (when it lands): `SymbolOverlap_BothEditingSameFunction_ShouldNameTheSymbol` — tree-sitter fixture pair editing `AuthService.Login` → warning carries the symbol; disjoint functions in one file → downgraded to soft.

---

## TI-P2-20 — Agent commit-stream curation

**Files:** `CommitCuratorTests.cs` (pure planner fixtures), `CurationIntegrationTests.cs` (fixture worktree branch, `RequiresGitCli`).

1. `Plan_WipFolding_ShouldFoldCheckpointsIntoNearestMeaningfulAncestor` — fixture commit lists (wip-run in the middle, wip at branch tip, interleaved) → exact `RebaseTodoItem` sequences.
2. `Plan_AllWipBranch_ShouldYieldSingleSquash_WithGeneratedConventionalSubject`.
3. `Plan_ShouldRewordSurvivors_ToConventionalCommitForm` (via the T-31 builder — assert the builder is called, not a re-implementation).
4. `Plan_ShouldPreserveProvenanceTrailers_OntoSquashedResults` — `Agent:`/`Task:` trailers from folded commits survive on the surviving commit.
5. `Plan_MergeCommitInRange_ShouldRefuseTyped` (same T-08 restriction).
6. `Curate_RunningAgent_ShouldBeBlocked_OnlyAwaitingReviewOrPaused` (state guard unit).
7. Integration: `Curate_ShouldExecuteViaInteractiveRebaseService_UnderYieldDiscipline` — real fixture branch of 6 commits (3 wip) → curated history matches the plan's before/after preview; the execution path is `IInteractiveRebaseService` (no second rebase driver — assert by seam).
8. `PostCuration_ShouldMarkBranchUnverified_AndRequireReverify` — the staleness handoff into P2-10.

---

# E. PLATFORM TRACK — M8 + continuous (P2-23…P2-26)

---

## TI-P2-23 — RBAC / SSO / SCIM

**Files:** `RolePermissionTests.cs` (pure), `RbacInterceptorTests.cs` (daemon in-proc), `ScimRoundTripTests.cs`, `PolicyDocTests.cs`.

1. `RoleWithoutApproveMerges_ShouldGetPermissionDenied_OnMergeRpc_EvenFromHandCraftedClient` — the master doc's named invariant, raw stub client.
2. `PermissionMatrix_ShouldEnforceEveryRoleRpcPair` — `[Theory]` over role × permission-gated RPC (spawn/approve-plan/approve-merge/edit-egress/edit-budgets).
3. `OidcGroupMapping_ShouldResolveRoles_FromFixtureTokens`.
4. `Scim_CreateDeactivate_ShouldRoundTrip` against the SCIM test harness; deactivated user's calls → denied immediately.
5. `PolicyDoc_SignatureInvalid_ShouldBeRejected`; `PolicyUpdate_ShouldPropagateWithoutDaemonRestart` — change budgets in the signed doc → gateway enforces the new value on the next lease, no restart.
6. `UiHidingIsNotEnforcement` — every UI-gated action has a matching interceptor test (checklist review rule, encoded as the matrix in case 2).

---

## TI-P2-24 — Supply-chain & secrets compliance

**Files:** `LockfileDeltaExtractionTests.cs` (pure, per-ecosystem fixtures), `LicenseGateTests.cs`, `VaultKeyStoreTests.cs` (`RequiresDocker` dev-mode Vault).

1. `ExtractDelta_Npm_Pnpm_NuGet_ShouldListAddedAndChangedPackages` — `[Theory]` per ecosystem fixture pair.
2. `SpdxLookup_ShouldResolveLicenses_FromLocalDatabase` (offline).
3. `CopyleftHeuristic_ShouldFlagGplAndAgpl_AsBlockingReviewCategory` — feeds the P2-11 flagged panel; `AgplIntroducingBranch_ShouldBlockMergeUntilAcknowledged` (extends the P2-11 end-to-end).
4. `VaultKv2_SaveRetrieveDelete_ShouldRoundTrip` against a dev-mode Vault container; `AwsSecretsManagerBackend_ShouldSatisfySameContractSuite` — one shared contract test class runs against **every** `ISecureKeyStore` implementation (DPAPI, Vault, ASM-mock).
5. `GateRunsAtVerified_ShouldNotBlockEarlierStates` — queue-integration placement test.

---

## TI-P2-25 — Cloud worktrees guardrails

Guardrails are CI checks, tested as contract:

1. `WanLatencyJob_ShouldRunP2_14SuiteUnder80msNetem_PerRelease` — the job definition exists and is release-blocking; its content is TI-P2-14's suite unchanged (any test edited *only* to pass WAN is a review rejection).
2. `Protos_ShouldCarryNoLocalhostAssumptionsOrFilesystemPaths` — proto-descriptor sweep: no field named/typed as a daemon-local path except documented opaque handles (G-14 automated where checkable; the rest is the review rule).
3. `TerminalEcho_ShouldStayUnder100msAt80msRtt` — measured in the WAN job on the P2-18 engine (acceptance number from the master doc).

---

## TI-P2-26 — VibeOrchestrator engine + stream interception

**Files:** `StreamPatternMatcherTests.cs` (pure, over recorded transcripts), `CircuitBreakerTests.cs` (pure), `VibeInterceptionIntegrationTests.cs` (scripted dev server).

1. `PortHarvest_ShouldMatchLocalhostUrls_AcrossRecordedTranscripts_AnsiStripped` — `[Theory]` over checked-in real dev-server transcripts (vite, next, dotnet) with ANSI codes intact → `[APP_READY_ON_PORT_X]` with the right port; ANSI stripping is the matcher's job, asserted.
2. `OAuthUrlDetection_ShouldEmitAuthRequired_WithAgentUuidState`.
3. `ErrorInterception_ShouldInjectFixPrompt_IntoAgentStdin_BytesNeverLeaveVm` — the interception happens daemon-side; the client stream shows no raw error bytes routed outward (seam assertion).
4. `Breaker_ThreeIdenticalTraceHashes_ShouldTrip`; `Breaker_FiveErrorsInTenMinutes_ShouldTrip` (virtual clock); `Breaker_DistinctErrors_ShouldNotTrip`; normalization: `TraceHash_ShouldBeStable_AcrossAddressesAndTimestamps`.
5. `BreakerTrip_ShouldDockerPause_AndEscalate` (scripted crashing dev server, `RequiresDocker`).
6. `ChatBridge_Rpc_ShouldRelayPromptsToAgentStdin` (daemon in-proc).

---

# F. COMPETITIVE-MATCH WAVE — M7.75 (P2-27…P2-45)

---

## TI-P2-27 — Ticket-to-verified-PR pipeline

**Files:** `TicketIntakeTests.cs` (fixture transports per provider), `TicketClarityCheckTests.cs` (pure), `TicketPipelineEndToEndTests.cs` (scripted-swarm).

1. `DraftPlan_PerProvider_ShouldMapTicketFieldsToTaskPlan` — `[Theory]` over recorded fixtures: GitHub issue, GitLab issue, Jira, Linear, Azure Boards, monday.dev → `Scope/Approach/TestStrategy` populated; provider tokens header-only (transport fixture asserts, mirroring T-23's pattern).
2. `ClarityCheck_ShouldGradeScopeAndAcceptanceCriteria` — fixtures for missing AC, vague scope, conflicting labels → gap list + suggested questions; a clean ticket → no gaps (assistance, never silent rejection — no code path discards a ticket).
3. `RoutingRules_ShouldOfferOnlyMatchingTickets` — label/status/query filter matrix.
4. `PlanApproval_ShouldNeverBeSkipped_ForTicketInitiatedWork` — API-shape + integration: no intake path reaches `SpawnWorker` without an approved plan.
5. `TicketEditedAfterDraft_ShouldShowStalenessChip` (VM).
6. `ReportOutcome_ShouldPostCommentWithLinks_AndOptionalTransition` — fixture write-back per provider; `WriteBackWithoutPermission_ShouldFailTyped_NonFatal` (pipeline completes, failure surfaced).
7. `TwoWorkersFromSameTicket_ShouldBeAllowedAndLabeled` (comparison flow marker for P2-31).
8. `EpicImport_ShouldCreateMultiTaskPlan_AndResyncOnTrackerChange` — fixture Jira epic → P2-28 children; re-sync diff after the tracker fixture changes.
9. End-to-end: `Ticket_ToPlan_ToWorker_ToVerified_ToMerged_ToComment` — the full scripted loop; provenance trailer and Agent Trace record carry the ticket id; the verification record links back (assert the chain of references).

---

## TI-P2-28 — Multi-repo tasks + epic slices

**File:** `MultiRepoTaskTests.cs` (two `DualRepoFixture`s, scripted-swarm).

1. `TwoRepoTask_ShouldSpawnOneWorkerPerRepo_WithSharedContextInjected` — both workers' prompt context contains the shared brief.
2. `CrossRepoGate_ShouldBlockMergeOfRepoA_WhileRepoBUnverified` — the "all repos verified" gate; verifying B unblocks A.
3. `OneWorkerFails_ShouldShowPartialState_OthersUnaffected`.
4. `RepoRemovedFromDiskMidTask_ShouldFailTyped_TaskRecoverable`.
5. `SharedContextEditedMidFlight_ShouldReachWorkersAtNextYield_NeverMidGeneration` — timeline assertion via the harness.
6. `NoCrossRepoGitOperations` — each repo's ref state changes only via its own worker/merge (`CaptureRefState` per repo).
7. Slices: `Slice2_ShouldNotStartUntilSlice1Merged`; `SliceControls_PauseResumeReplanSkipRetry_ShouldTransitionCorrectly` (state-machine table); `SliceOrdering_ShouldSerializeMeasuredOverlap` — two slices whose branches P2-19 flags as conflicting → serialized even without a declared dependency.

---

## TI-P2-29 — Session board & candidate comparison

**Files:** `SessionBoardProjectionTests.cs` (pure/VM), `ComparisonViewModelTests.cs` (VM), plus a render-harness PNG.

1. `Board_ShouldProjectQueueStatesIntoLanes_WithBadges` — fixture agents across every `WorkerMergeState` + `Conflict`/`RateLimited` badges → correct lanes; **zero new lifecycle state** appears anywhere (projection only).
2. `Drag_ShouldOfferOnlyLegalTransitions` — `AwaitingReview → Working` (with follow-up prompt) allowed; `Working → Merged` not offered; illegal drops are no-ops.
3. `Comparison_ThreeFixtureBranches_ShouldShowDiffVerificationSpendProvenance` — side-by-side panes populated from fakes.
4. `PickWinner_ShouldArchiveOthers_ViaQueueRejectionPath` — losers hit the P2-10 rejection path (branch delete + prune asserted through the queue fake).
5. Render harness: board PNG with populated lanes in light + dark themes.

---

## TI-P2-30 — Automations, scheduling & agent fleets

**Files:** `AutomationTriggerTests.cs` (pure, virtual clock), `FleetPolicyTests.cs` (pure), `AutomationEndToEndTests.cs` (scripted-swarm).

1. `CronTrigger_ShouldFireOnSchedule_VirtualClock`; `RepoEventTrigger_ShouldFireOnMatchingEvent`; `TicketLabelTrigger_ShouldIntakeViaP2_27`.
2. `TriggerStorm_ShouldDedupAndRespectAdmission` — N simultaneous CI-failure events → one run per distinct failure hash, spawns capped.
3. `PolicyAutoApprove_ShouldRequireExplicitOrgPolicy_AndEmitAuditEvent` — default is `AlwaysAsk`; auto-approve without the policy → refused; with it → `AuditProbe` shows the policy-attributed approval.
4. `Fleet_DailyPrRunCap_ShouldPauseAtCap`; `Fleet_OpenReviewCap_ShouldPauseWhileNBranchesUnreviewed_AndResumeOnReview` — the reviewer-flooding control, both directions.
5. `Fleet_MandateScopeBudgetChanges_ShouldBeAuditEvents`.
6. `EditAutomationWhileRunLive_ShouldApplyNextRun`; `DisabledAutomation_ShouldRetainHistory`.
7. `FleetOutput_ShouldEnterMergeQueue_AndStaleInvalidateLikeAnyBranch` — a fleet branch goes `Verified → StaleVerified` when main moves (the "beat" claim, tested).
8. End-to-end: `ScriptedNightlyDependencyBumpRun_ShouldProduceVerifiedBranchThroughGovernedPipeline`.

---

## TI-P2-31 — Dispatcher & multi-candidate runs

**Files:** `DispatcherRoutingTests.cs` (pure), `MultiCandidateTests.cs` (scripted-swarm).

1. `Route_ShouldFollowRoutingTable_OrgDefaultsAndCapabilityMetadata` — table-driven; `Auto_ShouldRouteByTemplateAndPastSuccessTelemetry` (fixture telemetry; heuristics only — no ML dependency appears).
2. `NCandidates_ShouldSpawnInParallel_CappedByAdmissionAndBudget` — request 4 with headroom 2 → 2 spawn, 2 queue/reject typed.
3. `Candidates_ShouldLandInComparisonView_WithVerificationSpendAndRiskScore` — the "compared on outcome + spend + risk" beat.
4. `WinnerMerges_OthersRejected_ThroughStandardPaths` — no bespoke teardown.
5. `AdapterHealthDegraded_ShouldExcludeCliFromAutoRouting` (fixture adapter-channel metadata).

---

## TI-P2-32 — Public CLI + SDK + MCP server + webhooks/chat

**Files:** `SdkContractTests.cs` (generated SDKs against `DaemonFixture`), `McpServerTests.cs`, `WebhookDeliveryTests.cs`.

1. `GeneratedCSharpSdk_ShouldDriveInProcDaemon_ListSpawnQueueVerificationAudit` — the contract test the master doc names; the TypeScript SDK runs the same scenario in a scripted node step (CI Linux leg).
2. `Sdk_ShouldHaveNoPrivilegedBypass` — SDK calls hit the same interceptors: wrong token → denied; role limits (P2-23) apply.
3. `McpToolCall_SpawnWorkerOnTicket_ShouldCreateGovernedTask_WithPlanApprovalAndAuditEvents` — MCP-initiated work goes through plan approval + budgets (the governed agent-of-agents claim); `AuditProbe` sequence identical to a UI-initiated task.
4. `McpToolSurface_ShouldReuseActionRegistry` — tool list derives from T-18's registry (seam assertion, no second registry).
5. `Webhook_ShouldDeliverQueueTransitionsEscalationsBudgetEvents_PerRouting`; `Webhook_SinkDown_ShouldRetryBounded`; `WebhookPayload_ShouldCarryLinksAndMetadata_NeverDiffContentByDefault` (schema test).
6. `SlackTeamsTemplates_ShouldRenderEventFixtures` (snapshot).

---

## TI-P2-33 — Dev-server preview & port panel

**File:** `PortPanelTests.cs` (VM over fixture P2-26 streams), preview smoke in the headless harness.

1. `PortHarvestStream_ShouldSurfaceChipsOnAgentCard` — fixture `[APP_READY_ON_PORT_X]` events → chips with ports.
2. `AgentTeardown_ShouldCloseAllForwards` — forward lifecycle bound to the agent context (daemon test).
3. `MultiplePorts_ShouldListAll_ClickSelectsTarget`.
4. `PreviewNavigation_Smoke_ShouldLoadForwardedPort` (headless harness against a scripted local server).

---

## TI-P2-34 — Context vault

**Files:** `ContextVaultIndexTests.cs`, `ContextPackTests.cs` (integration on `DualRepoFixture` bare mirrors), `VaultSourcesTests.cs`.

1. `Index_ShouldWalkBareRepoObjects_SymbolsDocsAndRuleFiles` — fixture repo with C# (Roslyn symbols), a tree-sitter language, markdown, `AGENTS.md` → expected item kinds; plain-text fallback for an unknown language.
2. `Index_ShouldReadBareObjectsOnly_NeverWorktrees_NoLocks` — same guard style as TI-P2-19.6.
3. `DeltaSync_RenameDeleteModifyMatrix_ShouldReindexOnlyTouchedPaths` — `[Theory]`; instrument the indexer to count re-indexed paths.
4. `BuildPack_ShouldEnforceBudget_AndScopeRules` — include/exclude paths honored; pack size ≤ budget (property-style over random budgets).
5. `EvidenceItems_ShouldPinCommitSha_TheyWereReadAt` — advance the repo after indexing → existing pack items still reference the old SHA and resolve to the old blob (immutability).
6. `Pack_ShouldBeImmutablePerTask` — rebuilding for the same task id → new pack, old untouched.
7. `RulesFileChangedMidTask_ShouldPinShippedDigest_NextTaskGetsNew`.
8. `BinaryAndHugeFiles_ShouldBeSkipped_ByClassifierAndSizeCaps`.
9. `ExternalSourceUnreachable_ShouldBuildDegradedFlaggedPack`; external tokens header-only (transport fixture, G-4).
10. `ReprovisionedRepo_ShouldTriggerFullReindex`.
11. Review integration (with P2-11): `Evidence_ShouldLinkToBlameAndHistoryAtRecordedSha` — the Git-native "beat" claim: navigating an evidence item lands on T-11/T-12 at the pinned SHA.

---

## TI-P2-35 — Verification depth: repair loop, Diff Guard, AI review

**Files:** `RepairLoopTests.cs` (scripted-swarm), `DiffGuardTests.cs` (pure corpus), `AiReviewServiceTests.cs` (fixture gateway), `FlakeDetectionTests.cs`.

1. `Repair_FirstAttemptFixes_ShouldReverifyGreen` — scripted failing test + scripted agent that "fixes" on the repair prompt → new `VerificationRecord` passed; prompt content contains failure tail + failing test names + plan scope (assert the prompt payload).
2. `Repair_CapReached_ShouldSurfaceNormalFailure` — default 2 attempts, then stop; `Repair_IntroducesNewFailure_ShouldStillAdvanceCounter_NoInfiniteAlternation` (alternating failure fixture).
3. `Repair_EveryAttempt_ShouldBeJournaledAndAudited` — `AuditProbe`: `repair_attempted` × N, no more.
4. `Repair_ShouldRunInSameWorkerSandbox_NeverElsewhere`; `Repair_ExternalPrEntries_ShouldBeOffUnlessOrgEnabled`.
5. `FlakyTest_SameHashPassesOnBareRerun_ShouldMarkRecordFlaky_AndNotSpawnRepair`.
6. `DiffGuard_Corpus` — `[Theory]`: oversize line volume; files outside plan scope; bulk generated files; lockfiles exempted into their own category; clean diff → not blocked; `PlanlessManualRun_ShouldApplyVolumeRulesOnly_SkipScopeRules`.
7. `DiffGuardVerdict_ShouldFeedCanMerge_BesideFlaggedGate` (queue integration); blocked work routes to the cockpit, never discarded (no deletion path — API shape).
8. `AiReview_ShouldProduceFindings_RenderedInDistinctLane` (fixture findings); `AiReview_IsAdvisoryOnly_NeverAMergeGate` — findings present, `CanMerge` unaffected; `AiReview_TimeoutOrBudgetExhausted_ShouldShowUnavailable_VerificationOutcomeUnaffected`; `AiReview_ShouldBeBudgetedThroughGateway` (lease observed).

---

## TI-P2-36 — Governed lessons

**File:** `LessonsServiceTests.cs` (integration: repo file + daemon state).

1. `Propose_ShouldEnterReviewState_NotEnabled`; `SetEnabled_ShouldRequireIdentity_AndEmitAuditEvent`.
2. `NoAutoEnable_WithoutExplicitOrgPolicy` — policy absent → any auto-enable path refuses; present → enabled with the policy recorded in the audit event.
3. `EnabledLessons_ShouldPrependIntoContextPacks_ViaRulesDigest`; `Pack_ShouldPinLessonsDigestItShippedWith` (change lessons after pack build → digest unchanged on the pack).
4. `Lessons_ShouldLiveInRepoFile_HumanEditableAndPrAble` — `.gitloom/lessons.md` round-trip; repo without `.gitloom/` → created on first enable.
5. `DuplicateLessons_ShouldDedupByContentHash`.
6. `Propose_WithSecretContent_ShouldBeRejected_ByT30Scan` — fixture lesson containing a token pattern → typed rejection.
7. `LessonReferencingRedactedAuditEntry_ShouldKeepWorking` (reference by id).

---

## TI-P2-37 — Session checkpoints, tree snapshots & forking

**Files:** `SessionCheckpointServiceTests.cs` (scripted-swarm on `DualRepoFixture`), `TreeSnapshotJournalTests.cs` (repo integration — the T-19 upgrade), `CrashResumeTests.cs`.

1. `Create_ShouldCaptureWorktreeShaDirtyTreeTranscriptAndEnvManifest` — dirty worktree → checkpoint carries a `DirtyTreeSha` (dangling-commit style) + transcript ref.
2. `Restore_ShouldRoundTripIncludingDirtyTree` — modify further, restore → workdir byte-identical to checkpoint time (tracked + dirty).
3. `Restore_ShouldRequireCompletedYield` — unpaused agent → typed refusal, nothing changes; `Checkpoint_MidRebaseWorktree_ShouldRefuseTyped`.
4. `Restore_WithNewerCommitsOnBranch_ShouldRequireConfirmation_WithDiffSummary` (VM-level gating).
5. `Fork_ShouldCreateNewWorktreeAtCheckpointSha_WithTranscriptSeededSession_AndRecordLineage`; `ForkOfFork_ShouldChainLineage`.
6. `Snapshots_ShouldBePinnedUnderRefsGitloomSnapshots_AndSurviveGitGc` — run `git gc --prune=now` → snapshot SHAs still resolvable; `RetentionPolicy_ShouldPruneOldSnapshots`.
7. `ScrollbackRefGcd_ShouldRestoreWithoutIt_Flagged`.
8. T-19 upgrade: `UndoMutatingOp_WithDirtyTree_ShouldNowRestoreUncommittedChanges` — the previously-refused dirty case from v1 TI-19.3 now succeeds via the pre-op tree snapshot (and the old refusal test is **updated in the same PR** — v1 standing rule 2 applies: weakening documented here, deliberately).
9. `SnapshotShas_ShouldPersistInJournalRows` — new column + migration round-trip.
10. `AdapterCrash_ShouldAutoCheckpoint_AndOfferReconstructedResume` — kill the scripted CLI (429/OOM-style exit) → checkpoint exists from last-known state; resume seeds a fresh session with the reconstructed context.

---

## TI-P2-38 — Review loop-closers

**Files:** `PatchIdStabilityTests.cs` (repo integration), `ReviewSessionServiceTests.cs`, `ReviewCoverageTests.cs` (pure), `CurationMigrationTests.cs`.

1. `PatchId_ShouldBeStableAcrossRebase` — comment + viewed-mark keyed by patch-id survive a fixture rebase (the foundational property everything else rides on).
2. `AddComment_SendToAgent_ShouldSerializeAsSteeringMessage_WithFileLineAndHunkContext`; `SendToAgent_WhileBusy_ShouldQueueViaP2_39`; `SendToAgent_ShouldNeverWriteToWorktree` (filesystem watch).
3. `AgentFollowUpCommit_ShouldLinkBackToComment_ViaTrailerOrTraceRef` — the comment→fix chain renders in the cockpit.
4. `MarkViewed_ShouldEmitHunkViewedReceipt_OnAuditChain` — append-only, per reviewer identity, carries `AtCommit`.
5. `Coverage_ShouldComputePercentViewedByRiskCategory` — pure fixture math; `CoverageReport_LinesMergedWithZeroHumanEyes_ByAgentModelDate` (the EU-AI-Act artifact — fixture-driven report content).
6. `CurationRewrite_ShouldMigrateCommentsViewedStateAndTraceRecords` — squash via P2-20 fixture → all three survive via patch-id/content-hash mapping; orphaned hunk (disappears in rebase) → comment preserved with context, flagged orphaned.
7. `TwoReviewers_ShouldGetReceiptsPerIdentity`; `PatchIdCollision_IdenticalHunks_BothMarked_Acceptable` (documented behavior pinned).
8. `SprintMode_DeferredHunks_ShouldRecordUnviewedInCoverage` (closes the TI-P2-11.12 loop).

---

## TI-P2-39 — Orchestration UX pack

**Files:** `MessageQueueTests.cs` (daemon), `PromptDispatchTests.cs` (VM), `SessionSearchTests.cs`, `PlanTreeParserTests.cs` (pure).

1. `Queue_ShouldDeliverFifoOnIdle_WhileAdapterStreams`; `Queue_ReorderAndCancel_ShouldApply`; `Queue_ShouldSurviveDaemonRestart` (persistence round-trip).
2. `PromptFirstDispatch_CoordinatorManaged_ShouldRouteThroughPlanApproval`; `PromptFirstDispatch_ManualMode_ShouldSpawnDirect_StillAdmissionCapped` — both modes, per the master doc.
3. `SessionSearch_Fts_ShouldRoundTripTranscriptsAndMetadata`; `Search_ShouldExcludeSecretMaskedRegions` — index a transcript containing a masked sentinel → zero hits (G-13 before indexing).
4. `AutoSummaries_ShouldGenerateAtSessionClose_AndBeSearchable`.
5. `PlanTreeParser_Fixtures_PerAdapter` — Claude Code + Codex stream-json fixtures → live tree structure; malformed events → skipped typed, never a crash; `PlanTree_IsReadOnly_InV1` (no mutation surface).
6. `ParsedEventStream_ShouldFeedAuditAndFlightRecorder_FromOneSubstrate` (seam assertion — no second parser).

---

## TI-P2-40 — Composer & review conveniences

**Files:** `ComposerAttachmentTests.cs`, `EditInPlaceTests.cs`, `ExternalEditorTemplateTests.cs` (pure), `RenderedPreviewTests.cs` (headless harness).

1. `PastedImage_ShouldLandOnlyInsideSandboxMount_AndReferenceByPath` — path is inside the agent mount; nothing written elsewhere.
2. `EditInPlace_SaveShouldAutoStage_RoundTrip` (repo integration); the edit surface is per-file only (not a project explorer — API shape).
3. `EditorTemplates_ShouldBuildArgumentList_NeverShellInterpolate` — `[Theory]` with hostile paths (spaces, quotes, `$(...)`, backticks) → `ProcessStartInfo.ArgumentList` entries verbatim, no shell string.
4. `MarkdownAndMermaid_Preview_ShouldRenderSmoke` (headless harness PNG; render-only — no editor controls present).
5. `VoiceDictation_PushToTalkBinding_ShouldBeRebindable` (T-18 map round-trip; the speech engine itself is manual-matrix).

---

## TI-P2-41 — Remote dashboard

**Files:** `DevicePairingTests.cs`, `RemoteRoleEnforcementTests.cs` (daemon in-proc over gRPC-web), `RemoteApprovalIdentityTests.cs`.

1. `Pairing_QrShortCode_ShouldMintScopedToken`; `Revocation_ShouldKillTokenImmediately` (next call denied).
2. `ObserveRole_ShouldReadEverything_MutateNothing` — `[Theory]` over every mutating RPC → denied; board/spend reads succeed.
3. `ApproveRole_RemotePlanApproval_ShouldLandWithPairedIdentity_InAuditChain` — the highest-value remote action, with the P2-15 identity assertion.
4. `LanBind_ShouldBeOptIn_WithTlsAndPinnedCert` — default binds localhost only; LAN bind requires the flag; the pairing-pinned cert is required for the connection.
5. `KillSwitchFromRemote_ShouldFanOutIdenticallyToLocal` (reuses the TI-P2-14.8 harness).
6. `PairingAndRevocation_ShouldBeAuditEvents`.

---

## TI-P2-42 — Merge-train simulation, verification cache & test-impact ordering

**Files:** `MergeTrainSimulationTests.cs` (scripted-swarm on fixtures), `VerificationCacheTests.cs` (pure keying + integration), `TestImpactOrderingTests.cs`.

1. `Train_FiveBranches_WithInducedTransitiveConflict_ShouldReportPerCarStatus` — the canonical fixture: cars 1–2 clean, car 3 conflicts only after 1+2 land → exactly car 3 flagged transitive.
2. `Train_ShouldRunInScratchWorktreesOnly` — agent worktrees and main untouched during simulation (`CaptureRefState` + directory watch).
3. `Train_InvalidatedByHumanMergeMidSimulation_ShouldResimulate`.
4. `CacheKey_ShouldBeMergeBaseShaPlusBranchTreeHashPlusTestCommandHash` — pure; any component changing → miss.
5. `CacheHit_AfterUnrelatedMerge_ShouldSkipRerun_ButStillRecordReceiptReferencingOriginal`; `FlakyReceipt_ShouldBeNonCacheable` (P2-35 flake detection integration); `Receipts_ShouldChainIntoAudit`.
6. `ImpactOrdering_ShouldRunImpactedSubsetFirst_FullSuiteBeforeMerge` — fixture coverage map → subset selection asserted; `PreliminaryVerdict_ShouldNeverGateMerge` (only full runs feed `CanMerge`); `ColdStartMap_ShouldFallBackToFullSuite`.

---

## TI-P2-43 — Per-agent signing identities

**File:** `AgentSigningTests.cs` (integration, reuses T-15 plumbing; `RequiresGitCli`).

1. `AgentCommit_ShouldBeSignedWithMintedAgentKey_VerifiableViaT15` — verify-commit fixture; identity `agent-<adapter>@gitloom-daemon`.
2. `SigningConfig_ShouldBeWorktreeLocalOnly_NeverUserConfig` — user global/local config untouched (compare before/after).
3. `HumanMerge_ShouldCountersignWithHumansOwnKey_ViaExistingJournaledMerge` — no new merge path (seam assertion).
4. `Badge_ShouldClassifyAgentKeyClass_DistinctFromHumanKeys` (T-15 badge mapping extension).
5. `KeyRotationMidBranch_ShouldListBothKeysValidForTheirWindows`; `RevocationList_ShouldBeRecordedInAudit`.
6. `CurationRewrite_ShouldResignWithCurrentKey` (P2-20 integration).
7. `AgentKey_ShouldNeverSignOutsideItsWorktree` — attempt via a second worktree → refused typed.

---

## TI-P2-44 — Sandbox health & exfiltration panel

**File:** `SandboxHealthPanelTests.cs` (VM over fixture log streams), `HealthTelemetryAuditTests.cs`.

1. `FixtureLogStreams_ShouldProjectPanelStates` — blocked egress, secret-file access attempt, anomalous process spawn, quarantine-remote push → each renders as the right strip/drill-down item ("tried to POST to pastebin at 14:02" fidelity: destination + process + time present).
2. `EveryTelemetryKind_ShouldEmitAuditEvent` (`AuditProbe`).
3. `Alerts_AreEventsNeverAutoKills` — no code path from an alert to container stop (API shape); the kill switch remains the only stop.
4. `TelemetryReadPath_IsReadOnly_OverProxyAndDaemonLogs` — no writes back into sandbox/proxy state.
5. `AlertRouting_ShouldReachP2_32Webhooks` (fixture sink).

---

## TI-P2-45 — Agent flight recorder

**File:** `FlightRecorderTests.cs` (scripted-swarm).

1. `RecordReplay_ScriptedSession_ShouldBeDeterministic` — record, replay twice → identical frame sequences.
2. `HunkToTimestampIndex_ShouldScrubToWritingMoment` — scripted session writing hunks at known times → selecting a hunk lands within the correct window, surrounding tool calls visible (via the P2-39 event stream).
3. `SecretMask_ShouldApplyBeforePersistence` — sentinel secret in the PTY stream → absent from the recording on disk.
4. `RetentionPruning_ShouldDropOldestRingBufferSegments`; recordings referenced from audit entries share P2-15 retention/redaction (redact → recording ref handled per policy).
5. `Playback_IsReadOnly_AndNeverReExecutes` — no process spawns during replay (process watch).

---

# G. CLIENT-PARITY TRACK (P2-C1…P2-C5)

> These may target `main` per the master doc's footnote; their tests follow **v1 conventions verbatim** (TempRepoFixture, `RequiresGitCli` where the CLI path is used, TI-00 for ViewModels). Decide the target branch per task with the repo owner.

---

## TI-P2-C1 — Interactive bisect assistant

**File:** `GitServiceBisectTests.cs` (integration, `RequiresGitCli`), `BisectLogParserTests.cs` (pure), `BisectWizardViewModelTests.cs` (VM).

1. `Bisect_ScriptedRegression_ShouldConvergeOnCulpritCommit` — fixture with a known bad commit among 16 → Good/Bad marks converge; culprit SHA exact (the offline end-to-end).
2. `MarkSkip_ShouldNarrowAroundUntestableCommit`.
3. `BisectLogParser_ShouldParseStateAndStepsLeft` — fixture `BISECT_LOG`s incl. skips.
4. `StartBisect_DirtyTree_ShouldRefuseTyped`.
5. `BisectHeadMoves_ShouldBeJournaled_AndResetRestoresPreBisectState` (T-19).
6. VM: `WizardProgress_ShouldTrackStepsLeft`; `CulpritCard_ShouldShowT32Context`.

---

## TI-P2-C2 — Global fuzzy search

**Files:** `SearchAggregatorTests.cs`, `GlobalSearchViewModelTests.cs` (VM).

1. `Aggregate_ShouldFanToCommitsBranchesTagsFilesAndHostItems_MergedRanking` — fixture sources; ranking uses the T-18 matcher (seam — no second matcher).
2. `Ranking_ShouldInterleaveByScore_NotBySourceOrder`.
3. `Debounce_ShouldCoalesceKeystrokes_AndCancelStaleQueries` (TCS-held fake sources; assert no stale results render — the T-11 gutter pattern).
4. `HostSourceUnavailable_ShouldDegradeGracefully_LocalResultsIntact`.
5. VM: `Enter_ShouldJumpToSelectedResult`; grouped rendering with highlights (render-harness PNG optional).

---

## TI-P2-C3 — Multi-repo dashboard + cross-repo "My Work"

**Files:** `WorkspaceOverviewServiceTests.cs` (integration over multiple fixtures), `DashboardViewModelTests.cs` (VM).

1. `Overview_ShouldReportBranchAheadBehindDirtyStashLastFetched_PerRepo` — `[Theory]` matrix over fixture states (the backlog's ahead/behind/dirty matrices).
2. `Cache_ShouldRefreshOnRepositoryChangedAndAutoFetch_NotOnEveryRead`.
3. `RemovedRepoOnDisk_ShouldShowTypedUnavailableCard_NotCrash`.
4. `NeedsAttentionLane_ShouldAggregateReviewRequestedAssignedIssuesFailingChecks_AcrossRepos` (fixture T-23…T-27 services).
5. `PersistedRepoSet_ShouldRoundTripAppDbContext`.
6. VM: quick actions route to the right repo's service (fake records target repo).

---

## TI-P2-C4 — Split-into-branches wizard & stacked restacking

**Files:** `SplitWizardPlannerTests.cs` (pure + property), `SplitWizardIntegrationTests.cs`, `StackedBranchTests.cs` (integration, `RequiresGitCli`).

1. `Partition_Property_SumOfGroupsEqualsOriginalDiff` — property-style over generated multi-file dirty states: no hunk lost, no hunk duplicated (the master doc's named property test).
2. `ProposedGroups_ShouldClusterByPathAndHunk`; user adjustment round-trip (move a hunk between groups → partitions still complete).
3. `GroupCommit_RoundTrip_ShouldProduceNBranches_EachWithOnlyItsGroup` — and the workdir ends clean; every cycle journaled + tree-snapshot-protected (undo restores the original dirty state — P2-37 integration).
4. `Restack_OnAmendOfA_ShouldReparentB`; `Restack_OnMergeOfA_ShouldReparentB`; `RestackConflict_ShouldRouteToT04Resolver`.
5. `Restack_ShouldAlwaysBeUndoable` (T-19 round-trip).
6. `StackVisualization_ShouldRenderOnGraph` (render-harness PNG).

---

## TI-P2-C5 — Client polish pack

One TI row per item (each lands with its item's PR):

1. **Mergetool:** `MergetoolVerb_ShouldOpenResolverWithFourPaths_AndWriteMergedOnResolve` — CLI arg plumbing + resolver round-trip on fixture files; registerable-config snippet documented, not automated.
2. **External difftool:** `DifftoolTemplates_ShouldBuildArgumentList_NeverShell` (same hostile-path `[Theory]` as TI-P2-40.3).
3. **Partial stash:** `StashSelectedPaths_ShouldStashOnlySelection_IncludeUntrackedToggle` (repo integration; backend shipped, UI wiring test).
4. **Patches/WIP share:** `FormatPatch_Am_ShouldRoundTripByteIdentically`; `SharePatchRef_ShouldPushRefsGitloomPatches_AndImportFlowRestoresIt` (bare-remote fixture).
5. **Templates/gitmoji:** composer snapshot tests — template insertion + gitmoji picker output (VM).
6. **Diff search:** `DiffCtrlF_ShouldFindAcrossHunks_AndHighlight` (VM; verify-absence-first noted in the PR).
7. **AI commit message:** `AiCommitMessage_ShouldEnforceConventionOnFixtureProviderOutput` — fixture provider returns a non-conforming message → composer normalizes/rejects per convention; key usage via P2-01 (never argv/log — reuse the TI-P2-01 scrub assertions).

---

# H. WAVE 3 — VIBE PRODUCT (P3-01…P3-05)

---

## TI-P3-01 — Auto-checkpoints + agent conflict resolution

**File:** `VibeCheckpointServiceTests.cs`, `AutoConflictResolutionTests.cs` (scripted-swarm on `DualRepoFixture`).

1. `NChatTurns_ShouldProduceNTreeValidCheckpoints` — each `git fsck`-clean, message `Auto-Checkpoint: <summary>`.
2. `CheckpointIdentity_ShouldBeVibeAuthor_NeverUserGlobalIdentity` — compare signatures; user config untouched.
3. `GenerationFailsMidWrite_ShouldCreateNoCheckpoint_PreviousUntouched`.
4. `Restore_UnpausedAgent_ShouldRefuseTyped`; `Restore_ShouldBeJournaled_AndItselfUndoable` (undo-of-restore round-trip).
5. `InducedConflict_ScriptedAgentResolves_ShouldFinalizeMerge` — three-way blobs fed via T-03 plumbing; `AuditProbe`: `conflict_auto_resolved`.
6. `AgentResolvesIncorrectly_TestsFail_ShouldMarkCheckpointVerifiedGreenFalse`.
7. `Unresolvable_ShouldEscalate_AndLeaveCleanConflictedState` — no half-finalized merge: index conflicted, no bogus resolution staged, no conflict markers committed as "resolved" (the rejection trigger, asserted).
8. `DeveloperModeAgent_ShouldNeverAutoResolve` — mode flag → conflicts surface to the human resolver (P2-09 path).
9. `CheckpointSpam_ShouldRateCap_MinIntervalAndMaxCount_OldestPruned` (virtual clock).
10. `Restore_IsGitNative_NeverDirectoryCopy` — seam assertion (rejection trigger).

---

## TI-P3-02 — Escalation UX (triage)

**Files:** `TriageViewModelTests.cs` (VM from a real breaker trip via the scripted agent), `DiagnosticBundleTests.cs`.

1. `BreakerTrip_ShouldPresentExactlyThreeActions` — and wording matches the copy deck (snapshot against the checked-in deck, not developer jargon by construction).
2. `TryDifferentApproach_ShouldRepromptWithFailureContext_AndDecayBreakerThreshold` — 2 identical hashes re-trip after reset.
3. `GoBackToWhenItWorked_ShouldRestoreLastVerifiedGreenCheckpoint_Journaled` — lands exactly on the last checkpoint preceding a green verification (fixture with green→red→red history).
4. `NoGreenCheckpoint_ShouldDisableOption2_WithHonestExplanation`.
5. `GetHelp_Bundle_ShouldContainTranscriptTailBreakerStateCheckpointsEnvSummary`; `Bundle_RedactionGrep_ShouldFindZeroKeyMaterial` — automated grep over the produced artifact using the T-30 scanner patterns with planted sentinels (the master doc's named automated test).
6. `BundleGeneration_WithLivePty_ShouldSnapshotWithoutPausing`.
7. `RepeatedEscalations_ShouldShiftOption1TextTowardOption3`.
8. `EveryEscalationAndChosenAction_ShouldBeAuditEvents`; `RawStackTraces_ShouldBeBehindExpanderOnly`.

---

## TI-P3-03 — Vibe UI: mode toggle, chat, live preview

**Files:** `VibeModeToggleTests.cs`, `ChatCardTests.cs` (VM), `VibeUiRenderHarness.cs` (headless), `LivePreviewIntegrationTests.cs` (scripted dev server).

1. `ScaffoldFlow_ReadyEvent_ShouldNavigatePreviewToForwardedPort` (scripted dev server through the P2-26 tap + P2-33 forward).
2. `ChatCards_ShouldRenderFromEventFixtureStream` — checkpoint-created / verifying / escalation cards from a fixture orchestrator stream.
3. `ModeToggle_RoundTrip_ShouldPreserveSessionState` — Developer → Vibe → Developer in the headless harness; dock layout and terminal session intact (the master doc's named harness test).
4. `ModeToggleMidGeneration_ShouldNotInterruptAgent` (harness timeline).
5. `MultipleDevServers_ShouldOfferPortPickerChip`; `PreviewCrash_ShouldOfferReload_SessionUnaffected`.
6. `VibeActions_ShouldRouteThroughSameJournaledAuditedServices` — `AuditProbe` sequence for a Vibe restore equals developer-mode restore (no privileged shortcut).
7. `ModeIsViewState_NotDataMigration` — toggle writes no DB migration/content changes (DbContext diff).
8. Render harness: 2-pane Vibe layout PNG, light + dark.

---

## TI-P3-04 — One-click deployment

**Files:** `DeployProviderTests.cs` (recorded fixtures), `PublishFlowTests.cs`, live smoke (`RequiresNetwork`).

1. `Provider_CreateTriggerPollFail_Fixtures` — `[Theory]` per provider (Vercel, Netlify): create project, trigger, poll to success, poll to failure → typed results.
2. `Publish_ShouldCheckpointThenPushThenTriggerThenPresentUrl` — ordering asserted; push uses the existing authenticated path.
3. `FirstPublish_NoRepo_ShouldCreateRepoViaHostApi` (T-23 transport fixture).
4. `BuildFailure_ShouldRouteToTriage_WithRedactedProviderLogTail` — redaction grep on the attached tail.
5. `RePublish_ShouldReuseProject_NewDeploy`.
6. `Tokens_ShouldLiveInKeyringOnly_NeverArgvUrlOrLog` — the token-storage audit test: run a full fixture publish, grep captured argv/logs/URLs for the sentinel token → zero.
7. `Publish_IsExplicitOnly_NeverAutomaticOnCheckpoint` — N checkpoints → zero deploy calls.
8. Live end-to-end against a real test account (`RequiresNetwork`, nightly/release).

---

## TI-P3-05 — GitLoom Web

**Files:** `WebContractTests.cs` (web shell against a pod over the unchanged protos), `SessionOriginIsolationTests.cs`, `AdoptSessionTests.cs`.

1. `WebShell_ShouldDriveCloudPod_ThroughUnchangedProtoSuite` — the contract test: the same daemon-in-proc scenarios pass over gRPC-web with **zero proto changes** (descriptor hash compared).
2. `PreviewIframes_ShouldBeSandboxed_PerSessionOrigin_NoCrossSessionBleed` — two sessions → distinct origins; a cross-origin fetch from one preview to the other fails.
3. `WebActions_ShouldLandInSameAuditChain_AsDesktop` — identity-attributed events, chain verifies.
4. `AdoptSession_WebToDesktop_ShouldRoundTrip` — open a web session locally; state (queue position, terminal snapshot) intact.
5. `BrowserStorage_ShouldHoldSessionCookieOnly_NoSecrets` — storage inspection after a full session (rejection-trigger guard).
6. `NoWebOnlyOrchestratorFork` — the pod runs the same daemon binary (build-artifact identity check in CI).

---

# I. WAVE 4 — CLOUD, ECOSYSTEM, HOST PARITY (P3-06…P3-10)

---

## TI-P3-06 — Cloud worktrees implementation

**Files:** `CloudPodSuiteRuns.cs` (the WAN suite against a real pod — **the** acceptance), `TenantIsolationTests.cs`, `MeteringTests.cs`, `CryptoShredTests.cs`.

1. `UnchangedP2_14Suite_ShouldPassAgainstCloudPod_At80msRtt` — already in CI as the P2-25 guardrail; now against a real pod.
2. `NetworkDropMidSession_ShouldReattachTerminalViaSnapshot_QueueStateIntact` (P2-18 path).
3. `PodEvictionRestart_ShouldReattachOrCleanDead_NeverSilentLoss` (session-leader pattern re-test in-cloud).
4. `TwoTenants_ShouldHaveZeroCrossRead` — tenant A's client enumerating/reading anything of tenant B → denied at every surface (repos, audit, terminal).
5. `RepoBytes_ShouldLeaveTenantBoundaryOnlyViaUsersOwnPushOrProviderApi` — egress rules in-cloud (fixture exfil attempt from a pod sandbox → denied + audited).
6. `Metering_ShouldMatchScriptedSessionComputeAndStorage` — scripted session with known durations → spend events within tolerance.
7. `AccountDeletion_ShouldReapPodsExportAuditThenCryptoShred` — after key deletion, stored ciphertext is unrecoverable (decrypt attempt fails), export artifact complete.
8. `ClockSkew_ShouldOrderAuditByPodSequence_NotClientTime`.
9. `RemoteEnvironmentPicker_ShouldBePerRepo_NeverSilentDefault` (VM).

---

## TI-P3-07 — Host parity: GitLab / Bitbucket / Azure DevOps

One suite per host, **one host = one PR**, each mirroring the T-23 fixture pattern:

1. `Fixtures_ListCreateMergeClose_PerProviderInterface` — MRs/PRs, issues/work items, pipelines/checks, todos/notifications (or typed `unsupported` where the host has no API — Bitbucket notifications stays typed-unsupported and *only* that panel), releases.
2. `ErrorMapping_ShouldCoverRateLimitsAuthAndNotFound_Typed` per host.
3. `TokenNeverLeaks` — header-only transport assertion with sentinel tokens (same audit as `GitHubApiClient`), per host transport.
4. `IsSupported_Matrix_ShouldUpdatePerHostCapabilities` — the registry matrix test extended per host.
5. `P2_12Intake_ShouldWorkAgainstHostPrList_Unchanged` — the intake suite (TI-P2-12) re-run with the new host's fixture provider.
6. GitLab-specific: `MrApprovalsAndMergeTrains_ShouldSurfaceReadOnly`; the placeholder OAuth app id is gone (config assertion).
7. AzDO-specific: `PatDialogFlow_ShouldAuthenticate` (no device flow).
8. One live smoke per host (`RequiresNetwork`, nightly).

---

## TI-P3-08 — Skills marketplace (format-first)

**Files:** `SkillPackManifestTests.cs` (pure), `SkillPackSignatureTests.cs`, `SkillPackInstallTests.cs` (fixture sandbox, `RequiresDocker`).

1. `Manifest_SchemaCorpus` — valid; missing target CLIs; undeclared egress domains; oversized; unknown fields → typed results.
2. `Signature_Invalid_ShouldBlockInstall_BeforeAnyExtraction`.
3. `ExtraEgressDomains_ShouldRequireExplicitUserAcknowledgment` — same panel pattern as P2-11 (per-domain, not global).
4. `InstallUpdateRemove_RoundTrip_InFixtureSandbox` — installed via the adapter-channel mechanics; **no host-side code execution at any point** (process watch on the host during install — packs are data + prompts).
5. `InstalledPacks_ShouldBeAuditEvents`.
6. Registry policy checks (denylist domains, secret scan, size caps) — fixture submission corpus through the automated policy pipeline.

---

## TI-P3-09 — Governed CI/CD janitor

**File:** `JanitorTests.cs` (scripted CI fixture through the T-26 seam + scripted-swarm).

1. `MainCheckFailure_ShouldSpawnRepairWorker_ThroughTwoPhasePlanApproval` — plan auto-generated but approval still required by default; fix branch enters the P2-10 queue; ships via the P2-12 host-API merge path (full chain asserted).
2. `AutoApprove_ShouldRequireExplicitJanitorClassOrgPolicy_WithItsOwnAuditEvent`.
3. `FlakyFailure_SameHashPassesOnRerun_ShouldMarkFlakyAndNotSpawn`.
4. `RepeatedFailedRepairs_ShouldTripCircuitBreaker_AndEscalate` (P2-26 pattern reuse).
5. `TwoJanitorsForSameFailureHash_ShouldDedupToOne`.
6. `ReleaseBranchFailures_ShouldBeInScope_FeatureBranchesNot` (watch-scope config test).

---

## TI-P3-10 — Team collaboration layer

**Files:** `SharedQueueVisibilityTests.cs`, `PermissionBoundaryTests.cs`, `OrgDashboardTests.cs`.

1. `SharedQueue_ShouldBeOptInPerRepo` — default invisible; opt-in → org members see `AwaitingReview` branches.
2. `PermissionBoundary_NoContentLeak_ToMembersWithoutRepoAccess` — the master doc's named test: a member without host-side repo access sees metadata presence rules only, never diff/file content (host permissions as source of truth — fixture permission map).
3. `ReviewAssignment_ShouldRecordReviewerIdentity_InAuditChain`.
4. `OrgDashboard_ShouldAggregateFromFixtureTelemetry` — spend, verification pass rates, review latency, audit-export status; desktop-only org → local export path produces the same numbers.
5. `PolicyTemplateManagement_ShouldRoundTripSignedPolicyDocs` (P2-23 integration).

---

# J. Standing rules for extending this document

1. **Every new task added to Master Doc v2 gets a TI section here in the same PR** that adds the task — and the task's inline **Required tests** block and its TI section are written together (the block summarizes; this document enumerates). No TI section, no task.
2. Test cases here are contracts: renaming is fine; deleting or weakening requires the same review rigor as changing a public API. TI-P2-37.8 is the template for a *deliberate* weakening: the superseded v1 test is updated in the same PR and the change is recorded in both documents.
3. When a bug escapes to `phase2` (or later `main`), the fix PR adds the regression test **and** back-fills the missing row in the relevant TI section.
4. The shared fixtures of A.4 are infrastructure contracts like TI-00 was: a Phase-2 test hand-rolling what a fixture provides is a review rejection.
5. When `phase2` merges into `main`, this document and the v1 strategy merge into one document in the same PR (the §A sections union; TI numbering preserved), and this rule is replaced by the merged document's own standing rules.
