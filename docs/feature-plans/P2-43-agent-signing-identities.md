# P2-43 — Agent Identity: Per-Agent Signing Keys & Countersigned Merges — Implementation Plan

**Task ID:** P2-43 · **Milestone:** M7.75 · **Priority:** P1 differentiator (novel —
Git-object-level attribution).
**Depends on:** T-15 (signing, shipped), P2-09, P2-15.
**Branch:** implement on `feature/P2-43-agent-signing-identities` off `phase2`; PR targets
`phase2`.

> **Verification profile:** Fully automated — signature verification fixtures, worktree-local config isolation, rotation windows, curation re-sign (`RequiresGitCli`); no human step.
>
> **Source of truth:** §P2-43 of `docs/phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md`.
> **Beat:** Agent Trace records (P2-11) are annotations; signatures are cryptographic and travel
> with the Git objects through clone/fork/push.

---

## 0.a Binding companions (2026-07-12 refresh)

This plan was refreshed against the master doc as consolidated on `phase2` at `0f80d21`
(2026-07-12), and this branch now carries that baseline via the merge commit in its history:
the Lane-H engineering pass (1,115-test suite, zero-warning build, [ADR-001...007](../phase-2/ADRs.md)),
the design corpus under `docs/design/`, and the orchestration hardening specs under `docs/phase-2/`.
The items below are **binding** alongside this plan. Where this plan and a companion disagree,
the master doc wins -- and fix the drift here in the same PR.

| Companion | What binds |
|---|---|
| [Master doc](../phase-2/implementation_plans/GitLoom_Master_Implementation_Document_v2.md) §P2-43 | Contract, invariants, edge rows, rejection triggers -- the source of truth (note: the doc moved on 2026-07-11; older copies of this plan cited `docs/GitLoom_Master_Implementation_Document_v2.md`) |
| [Test strategy v2](../phase-2/implementation_plans/GitLoom_Test_Implementation_Strategy_v2.md) **TI-P2-43** | The binding expansion of this plan's test contract -- "a feature PR that does not satisfy its TI section is incomplete by definition." Where the table below and TI-P2-43 differ, implement the union. The §A.4 shared fixtures (`DaemonFixture`, `ScriptedAgentHarness`, `FakeModelEndpoint`, `DualRepoFixture`, `SandboxFixture`, `AuditProbe`) are infrastructure contracts: hand-rolling what a fixture provides is a review rejection |
| [`DesignSystem.md`](../design/DesignSystem.md) (2026-07 design pass) | Any UI surface this task ships: corrected lane palette, state-encoding icon gates, accessibility gates, motion grammar; surfaces route through the [design hub](../design/README.md) |

---

## 0. Context — what exists today

T-15 shipped commit signing + verification badges (SSH/GPG, `SignatureStatusParser`). Agent
commits are currently unsigned or signed with whatever the worktree inherits. This task mints an
SSH signing key per agent identity, scopes it to the agent's worktree via **local** git config,
classifies the badge distinctly, and keeps the human merge as the countersign.

### What you can rely on

| Fact | Where |
|---|---|
| Signing plumbing + verification badges + `SignatureStatusParser` | T-15, `GitLoom.Core/Services/SignatureStatusParser.cs` |
| Agent worktrees with isolated local config | P2-06 |
| Keyring (`ISecureKeyStore`) daemon-side | P2-01 |
| Journaled human merge (the countersign step — **no new merge path**) | P2-10 `ForegroundMergeService` |
| Audit chain (rotation/revocation records) | P2-15 |
| Curation re-signs (rebase rewrites) | P2-20/T-08 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Agents/Identity/AgentKeyService.cs` (mint/rotate/revoke per-agent-identity SSH signing keys; daemon keyring storage) |
| **Create** | `GitLoom.Core/Agents/Identity/AgentSigningConfigurator.cs` (worktree-local git config: `user.signingkey`, `gpg.format=ssh`, `commit.gpgsign=true`, identity `agent-<adapter>@gitloom-daemon`) |
| **Create** | `GitLoom.Core/Agents/Identity/AgentAllowedSigners.cs` (allowed-signers file generation incl. validity windows for rotation) |
| **Edit** | `SignatureStatusParser`/badge classification — recognize the agent-key class (distinct badge) |
| **Edit** | P2-06/P2-09 worktree setup + teardown (configure signing at create; revoke on identity retirement policy) |
| **Edit** | P2-20 curation executor (rewrites re-sign with the current key — verify, don't implement twice: rebase inherits the worktree config) |
| **Create** | `GitLoom.App` badge styling for the agent class (tokens in all five themes) |
| **Create** | `GitLoom.Tests/AgentKeyServiceTests.cs`, `AgentSignatureVerificationTests.cs`, `RotationWindowTests.cs`, `CurationResignTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- The daemon mints an **SSH signing key per agent identity** (`agent-<adapter>@gitloom-daemon`);
  every agent commit is signed with it — via **local repo config in the worktree** (T-15
  plumbing), never user-global config.
- On human merge approval, the merge commit is signed by the **human's own configured key** —
  attribution at the Git object level. The countersign step **is** the existing journaled merge;
  no new merge path.
- Verification badges (T-15) learn the **agent-key class** (distinct badge).
- Keys live in the **daemon keyring**; rotation policy + revocation list recorded in audit.

---

## 3. Implementation steps

1. **`AgentKeyService`:** identity = `agent-<adapter>@gitloom-daemon` (per adapter kind, not per
   ephemeral agent id — rotation makes finer grains impractical; record the agent id in the
   trailer/trace as before). Mint: ed25519 keypair; private key in the daemon keyring
   (`agentsign_<identity>`); public half + validity window into the allowed-signers store.
   Rotate: new key, old key's window closed (kept for verification); revoke: window terminated +
   revocation audit event. All three → audit (`agent_key_minted/rotated/revoked`).
2. **Worktree configuration:** at worktree create (P2-06 hook): write the private key to the
   worktree's private tmpfs/agent-inaccessible location — **no**: the agent process must be able
   to sign commits it makes, so the key lives where git-in-the-worktree can use it (the
   container's tmpfs, 0400) but the **quarantine remote (P2-06) means a leaked agent key can
   only ever sign — it authenticates nothing** (no push credentials attach to it). Local config:
   `git config --local gpg.format ssh`, `user.signingkey <pubkey-path>`, `user.email <identity>`,
   `commit.gpgsign true`. Never touches user config (edge row 1 — repo with signing disabled
   globally still gets local-scoped agent signing).
3. **Allowed signers + badges:** `AgentAllowedSigners` merges agent identities (with
   `valid-after`/`valid-before` from rotation windows) into the allowed-signers file the T-15
   verifier consults. `SignatureStatusParser` classification: signer identity matching
   `agent-*@gitloom-daemon` → `AgentSigned` status → distinct badge (new tokens in all five
   themes; component class in `App.axaml`).
4. **Countersign:** no code beyond verification — the human merge (P2-10) already signs with the
   user's configured key when repo signing is on; add the cockpit/queue surface showing
   "agent-signed N commits · merge countersigned by <user>" from the parser output.
5. **Rotation mid-branch (edge row 2):** both keys listed valid for their windows — verification
   of older commits stays green. **Curation/rebase re-signs** with the current key (worktree
   config applies during T-08 execution); test asserts post-curation signatures verify with the
   new key.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| repo with signing disabled (user-level) | agent signing still local-config-scoped; user config untouched |
| key rotation mid-branch | both keys valid for their windows; old commits verify |
| rebase/curation | re-signs with the current key |
| agent key file exfiltrated (threat model) | key signs only — no push credentials attached (quarantine remote); documented in the plan/security doc |
| revoked key | commits in its window still classify, marked `RevokedWindow` in the badge tooltip |

---

## 5. Invariants (MUST)

1. Agent keys never sign outside their worktree (config is local; key mounted only in that
   agent's sandbox).
2. The human countersign step is the existing journaled merge — no new merge path.
3. Key lifecycle (mint/rotate/revoke) audited.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `AgentCommit_SignatureVerifies` | fixture worktree commit → `git verify-commit`/parser green with agent identity |
| 2 | `Badge_AgentClassDistinct` | parser output for agent identity → `AgentSigned`; human key → existing classes |
| 3 | `UserConfig_Untouched` | global/user gitconfig byte-identical before/after worktree setup |
| 4 | `Rotation_WindowValidity` | commits before+after rotation both verify; allowed-signers windows correct |
| 5 | `Curation_Resigns` | squash via P2-20 → new commits signed by current key |
| 6 | `KeyLifecycle_Audited` | mint/rotate/revoke → chained events |
| 7 | `RevokedWindow_Classified` | revoked-window commit → badge + tooltip state |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** user-global config writes; a second signing implementation beside T-15 plumbing;
keys outside the keyring/tmpfs; a new merge path for countersigning.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~AgentKey|FullyQualifiedName~AgentSignature|FullyQualifiedName~RotationWindow|FullyQualifiedName~CurationResign"
grep -rn "config --global" GitLoom.Core/Agents/Identity/   # 0 hits
```

---

## 8. Definition of done

- [ ] Per-identity key mint/rotate/revoke (keyring, audited, validity windows).
- [ ] Worktree-local signing config; agent-class badges in all five themes; countersign surfaced.
- [ ] Rotation + curation re-sign proven; user config untouched.
- [ ] All edge rows green. `AGENTS.md` Repository Map updated. One task = one PR linking **P2-43**, base `phase2`.
