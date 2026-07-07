# P2-22 — Installer Part 2: Windows Integration, Loopback OAuth, Adapter Channel, Teardown — Implementation Plan

**Task ID:** P2-22 · **Milestone:** M7 · **Priority:** P0
**Depends on:** P2-21.
**Branch:** implement on `feature/P2-22-installer-windows-integration` off `phase2`; PR targets
`phase2`.

> **Source of truth:** §P2-22 of `docs/GitLoom_Master_Implementation_Document_v2.md` (binds
> strategy §§J-4–J-6). Security-relevant PR (global rule 4): OAuth + deep-link checks evidenced.
> The **pinned adapter channel** is a survival requirement (agent-agnostic, market v2 §5.4).

---

## 0. Context — what exists today

P2-21 gets the VM installed. This task finishes distribution: Explorer integration, the one
OAuth listener every token flow must use, the `gitloom://` protocol handler, the adapter channel
that installs agent CLIs at pinned versions inside the VM, and a clean uninstall that leaves the
user's machine exactly as found (minus GitLoom).

### What you can rely on

| Fact | Where |
|---|---|
| Installer skeleton + elevated helper + OOBE state | P2-21 |
| `WslRunner` + distro lifecycle verbs (G-12) | P2-05 |
| Existing token storage (`token_<host>` in `SecureKeyring`) + `CredentialResolver` | `GitLoom.Core/Security/` |
| Existing host OAuth/browser launch path (`SafeWebUrl`, `BrowserLauncher` — scheme-validated single launcher) | `GitLoom.Core/Security/SafeWebUrl.cs`, `GitLoom.App/Services/BrowserLauncher.cs` |
| Sandbox exec for in-VM installs | P2-07 |

---

## 1. Files to create / modify

| Action | Path |
|---|---|
| **Create** | `GitLoom.Core/Security/LoopbackOAuthListener.cs` (shared: ephemeral port, PKCE, `state`, single-use, 5-min timeout) |
| **Create** | `GitLoom.Core/Security/Pkce.cs` (pure: verifier/challenge generation S256) |
| **Create** | `GitLoom.App/Services/DeepLinkHandler.cs` (`gitloom://` — non-secret deep links only) + protocol registration in installer |
| **Create** | `GitLoom.Core/Agents/Adapters/AdapterManifest.cs` (+ JSON schema), `AdapterChannel.cs` (fetch/verify/install/health-probe at pinned versions) |
| **Create** | `installer/` additions: Explorer context-menu registration ("Open in GitLoom"), uninstaller (`GitLoom.Uninstall`) |
| **Edit** | existing host-auth flows → route token acquisition through `LoopbackOAuthListener` (one listener implementation, everywhere) |
| **Create** | `GitLoom.Tests/PkceTests.cs`, `LoopbackOAuthListenerTests.cs`, `DeepLinkHandlerTests.cs`, `AdapterManifestTests.cs`, `AdapterPinSimulationTests.cs` |
| **Edit** | `AGENTS.md` Repository Map |

---

## 2. Contract (binding)

- **Explorer context menus** written by install, removed by uninstall (per-user registry keys —
  no HKLM unless the install is machine-wide; default per-user).
- **RFC 8252 loopback + PKCE for every token flow** — shared `LoopbackOAuthListener`: ephemeral
  port on `127.0.0.1`, `state` validation, **single-use**, 5-minute timeout.
- **`gitloom://` handler for non-secret deep links only** (open repo/PR/agent views). **No token
  ever in a `gitloom://` URL** (grep + code-path test).
- **Pinned adapter channel:** `adapters.json` manifest (cli → version, install cmd, config shims,
  health probe) fetched from a GitLoom-owned channel, installed **inside the VM** at pinned
  versions, never `@latest`, updated independently of app releases (keeps perpetual-fallback
  licenses functional).
- **Clean uninstall:** `wsl --terminate GitLoomEnv` → poll → `--unregister GitLoomEnv`; registry/
  Scheduled-Task/appdata removal; user repos untouched; optional `gitloom-vm` remote removal
  (asks); personal distros untouched (G-12).

---

## 3. Implementation steps

1. **`Pkce` (pure):** verifier = 32 random bytes base64url; challenge = S256(verifier). RFC 7636
   test vectors.
2. **`LoopbackOAuthListener`:** `HttpListener` (or Kestrel-min) on `http://127.0.0.1:{ephemeral}/callback`;
   flow: generate PKCE + `state` → build the authorize URL (through the existing scheme-validated
   launcher) → await exactly **one** callback ≤ 5 min → validate `state` (constant-time) →
   exchange code + verifier for the token (host-specific exchange delegated to the caller) →
   listener disposed (single-use — a second hit gets 410). Errors typed (timeout, state mismatch,
   user denial). Token lands in `SecureKeyring` via the existing path — never logged, never in a
   URL we construct beyond the provider's own redirect.
3. **Migrate existing flows:** every current token acquisition (GitHub OAuth/device flows in
   Accounts) routes through this listener where the host supports loopback redirect; a second
   listener implementation anywhere is a rejection trigger.
4. **`gitloom://`:** installer registers the protocol (per-user `HKCU\Software\Classes`).
   `DeepLinkHandler.Parse(uri)` → typed commands (`OpenRepo(path-id)`, `OpenPr(host,repo,n)`,
   `OpenAgent(id)`); **reject** any URI containing query/fragment keys matching secret patterns
   (`token`, `code`, `secret`, `key`) — parse test enforces; unknown verbs ignored gracefully.
   Single-instance forwarding: second app launch with a URI forwards to the running instance
   (named pipe) — matches how the OAuth listener avoids the protocol handler entirely.
5. **Adapter channel:** manifest schema
   `{ adapters: [{ id, displayName, version, sha256, installCmd[], configShims[], healthProbe }] }`,
   signature/hash verified after fetch (HTTPS + content hash pin); `AdapterChannel.EnsureAsync(id)`
   runs the install **inside the VM** (P2-07 exec, base image devbox), verifies the health probe
   (`claude --version` etc. matching the pinned version), applies config shims (e.g. non-
   interactive flags). Channel URL + cache under appdata; refresh independent of app updates.
   **Pin simulation test:** manifest pins vX while "upstream" (fixture registry) publishes a
   breaking vY → installed adapter remains vX and healthy.
6. **Uninstaller:** ordered, each step tolerant: stop daemon → `--terminate GitLoomEnv` → poll
   state → `--unregister GitLoomEnv` → remove protocol/context-menu registry keys → remove
   Scheduled Tasks → remove appdata (offer keep-settings) → optionally remove the `gitloom-vm`
   remote from known repos (explicit checkbox; default off) → **never touches** user repos or
   other distros. Evidence matrix run on a machine with a personal distro.

---

## 4. Edge-case matrix (binding — each row needs a test)

| Case | Required behavior |
|---|---|
| OAuth callback with wrong `state` | rejected, typed error, listener still single-use (flow aborted) |
| no callback within 5 min | typed timeout; port released |
| second callback hit | 410; first result unaffected |
| `gitloom://…?token=x` | rejected by the secret-pattern guard; nothing launched |
| adapter upstream ships breaking `@latest` | pinned version unaffected (simulation) |
| uninstall with personal distros present | only `GitLoomEnv` unregistered; `wsl --list` diff proves it |

---

## 5. Invariants (MUST)

1. No token ever in a `gitloom://` URL (grep + code-path test).
2. Personal distros untouched by uninstall (G-12).
3. Pinned adapter unaffected by a breaking upstream release (simulated test).
4. One `LoopbackOAuthListener` implementation for every token flow.

---

## 6. Test contract

| # | Test | Assertion |
|---|---|---|
| 1 | `Pkce_Rfc7636Vectors` | verifier/challenge match spec vectors; base64url no padding |
| 2 | `Listener_StateMismatchRejected` | forged callback → typed error |
| 3 | `Listener_TimeoutAndSingleUse` | 5-min timeout typed; second hit → 410 |
| 4 | `DeepLink_ParseMatrix` | valid verbs parse; secret-pattern URIs rejected; unknown verbs ignored |
| 5 | `Manifest_SchemaAndHash` | valid/invalid manifests; hash mismatch → typed refusal |
| 6 | `AdapterPin_Simulation` | fixture registry breaking release → pinned install intact + probe green |
| 7 | Uninstall matrix (manual, evidenced) | registry/tasks/appdata gone; repos + personal distros intact; screenshots + `wsl --list` before/after in PR |

---

## 7. Rejection triggers / Reviewer script

**Rejection:** a second OAuth listener; tokens in URLs/deep links; `@latest` installs;
uninstall touching `/mnt/c` repos or other distros; HKLM writes in a per-user install.

```bash
dotnet build GitLoom.slnx
dotnet test --filter "FullyQualifiedName~Pkce|FullyQualifiedName~LoopbackOAuth|FullyQualifiedName~DeepLink|FullyQualifiedName~Adapter"
grep -rn "gitloom://" GitLoom.Core/ GitLoom.App/ | grep -i "token\|secret\|code="   # 0 hits
grep -rn "@latest" GitLoom.Core/Agents/Adapters/ installer/                          # 0 hits
grep -rn -- "--shutdown" installer/                                                  # 0 hits
```

---

## 8. Definition of done

- [ ] Shared PKCE loopback listener; existing flows migrated; single-use/timeout/state tests green.
- [ ] `gitloom://` non-secret handler + registration; secret-pattern guard.
- [ ] Adapter channel: schema, hash verification, in-VM pinned installs, config shims, health probes, pin simulation.
- [ ] Explorer menus + clean uninstall (evidence matrix with a personal distro).
- [ ] `AGENTS.md` Repository Map updated. One task = one PR linking **P2-22**, base `phase2`.
