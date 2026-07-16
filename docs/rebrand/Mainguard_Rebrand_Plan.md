# GitLoom → Mainguard: full-repo rebrand plan

**Status:** Phase 0 landed with this document (docs + marketing site). Phases 1–5 not started.
**Decision (2026-07-16):** the product is renamed **Mainguard**. Forcing functions: the GitLoom
name is held by gitloom.ai (Estonia), and the master doc already flagged MergeLoom (mergeloom.ai)
as a "-Loom" competitor. Domain snapshot on decision day: `mainguard.dev` / `mainguard.ai` /
`mainguard.app` unregistered (owner securing them); `mainguard.com` parked since 2004 (possible
aftermarket buy); `mainguard.io` taken. Known name collisions are outside our space (a dormant
South African CMMS vendor, a Singapore physical-security firm) — formal USPTO/EUIPO clearance in
Nice classes 9 + 42 is still owed before GA.

**Brand rules** live in `PRODUCT.md` (metaphor + tone guardrail) and `DESIGN.md` (north star,
theme names). Product naming: **Mainguard** (app + free client), **Mainguard Pro**,
**Mainguard Cloud** (formerly Weave). Theme renames: Midnight Loom → **Midnight Watch**,
Daylight Loom → **Day Watch**, Loom Aurora → **Aurora**; Command Deck and Atelier unchanged.

~1,000 files contain the string `gitloom` in some casing. The rename is therefore **phased by
blast radius**: strings a user sees (cheap, safe) → the GitHub repo (coordinated) → code
identifiers (huge but mechanical) → persisted/runtime identifiers (each needs a migration) →
external services. Each phase is one PR (or one coordinated pair) that leaves the repo green.

---

## Phase 0 — brand sources + marketing site ✅ (this PR)

`PRODUCT.md`, `DESIGN.md`, `site/**` (copy, wordmark, favicon, GateHero/LaneSpine/SuccessGate
animations, `/weave` → `/cloud` redirect, theme labels, `mainguard-theme` storage key with legacy
fallback), worker accepts interest id `cloud` alongside legacy `weave`. Deliberately **not**
touched: worker deployed name/URL/D1 database, `vite.config.ts` base (`/GitLoom/` must match the
repo name until Phase 2), `GITHUB_URL`, `og.png` (regenerate when the crafted logo SVG lands),
IP-hash salt (`gitloom:` — changing it only resets rate-limit continuity, not worth it).

## Phase 1 — user-visible strings inside the product (no identifier changes)

Safe by construction: nothing persisted or referenced by code changes meaning.

- App window titles, About text, OOBE wizard copy, tray/notification text, dialog strings.
- Theme **display names** in `GitLoom.App/Themes/*.axaml` resources and the theme picker
  (`Midnight Watch`, `Day Watch`, `Aurora`) — resource *keys* and file names stay for now.
- Installer/uninstaller UI strings (`installer/GitLoom.Installer`, `GitLoom.Uninstall`),
  Start-menu shortcut display name, Add/Remove Programs `DisplayName`.
- `README.md`, `CONTRIBUTING.md`, `docs/**` prose, `AGENTS.md` prose (map paths stay accurate —
  they still say `GitLoom.Core/…` until Phase 3, which is correct).
- Grep guard for the PR: no diff outside string literals / markdown / `.axaml` text resources.

## Phase 2 — GitHub repo rename + everything pinned to the URL (one coordinated PR)

GitHub redirects old clone/remote URLs after a rename, so this is low-risk, but these must move
in the same change:

- Rename `dsazykin/GitLoom` → `dsazykin/mainguard` (owner action in GitHub settings).
- `site/vite.config.ts` `base: '/GitLoom/'` → `'/mainguard/'`; `index.html` `og:url`/`og:image`;
  `site/src/config.ts` `GITHUB_URL`; footer repo label.
- `.github/workflows/*` checkout/paths are relative (fine); badge/links in README; the
  `deploy-site.yml` Pages target follows the repo automatically.
- Local remotes keep working via redirect; update `phase2`/branch protections notes in
  `AGENTS.md` §git hygiene if they name the repo.
- If `mainguard.dev` is live by then, prefer cutting Pages over to the custom domain here
  (CNAME file in `site/public/`), which makes the `base` change moot — decide at execution time.

## Phase 3 — code identifiers: solution, projects, namespaces (one atomic PR + freeze)

The big mechanical one. **Do not start while long-lived branches are in flight** — it conflicts
with every open PR. Sequence it into a quiet window after the current audit/PR series merges,
and freeze other merges for the day.

- `GitLoom.slnx` → `Mainguard.slnx`; project dirs/csproj: `GitLoom.Core` → `Mainguard.Core`,
  `.App`, `.Protos`, `.Server`, `.Server.Tests`, `.Tests`, plus `installer/GitLoom.Installer*`,
  `installer/GitLoom.Uninstall`. (Scratch projects `GitLoom.StyleConsole`/`StyleTests`/
  `AvaloniaTests` are not in the solution — rename or delete opportunistically.)
- Root namespace + `using` sweep: `namespace GitLoom.` → `namespace Mainguard.` etc. Pure
  textual rename; Rider/Roslyn rename or `git mv` + `sed`, then `dotnet build` is the oracle.
- `GitLoomPaths` class → `MainguardPaths` (the *values* it returns migrate in Phase 4, not here).
- Proto files: `option csharp_namespace` and gRPC service names. **Wire compatibility:** client
  and daemon ship together (same payload), so renaming gRPC service/package names is safe as
  long as UI + gitloomd are never mixed across this boundary — enforce by bumping the daemon
  handshake/contract version in the same PR (G-14 review point).
- EF Core: namespace moves must not perturb migrations — keep migration class names/ids frozen;
  verify `dotnet ef migrations list` and a cold DB creation. The SQLite file name itself is
  Phase 4.
- `AGENTS.md` Repository Map: wholesale path update in the same PR (the map must never drift).
- CI greps that name paths (`grep -rn … GitLoom.Core/ GitLoom.Server/ installer/` in G-12 etc.)
  and `docs/phase-2/**` verification scripts: update the literal paths in the same PR, or CI
  goes green-but-vacuous. This is the sneakiest part of the phase — audit every reviewer-check
  command in the master doc and workflows for hardcoded `GitLoom.*` paths.
- Expected size: thousands of files touched, zero behavior change, full suite green.

## Phase 4 — persisted & runtime identifiers (each item = its own migration decision)

These outlive the process, so each needs explicit back-compat. Recommended default for the
alpha stage (tiny install base): **migrate-on-first-run, keep read-fallback for one release,
delete the fallback at beta.** Inventory, with the migration shape per item:

| Identifier | Today | Migration |
|---|---|---|
| Windows data root | `%LocalAppData%\GitLoom` | On start: if old dir exists and new doesn't, move it; else fresh `…\Mainguard`. One-shot, logged. |
| Unix/VM data root | `~/.gitloom` (also holds `daemon.token`) | Same move-on-start inside the VM; `mainguardd.service` sets the new path; installer migrates during upgrade. |
| WSL distro name | `GitLoomEnv` | **Highest risk.** Options: (a) keep name as legacy plumbing, (b) re-register: export → import as `MainguardEnv` → unregister old (G-12: never `wsl --shutdown`). Recommend (b) in the installer's upgrade path only, with the OOBE resume-guard updated; fresh installs get the new name. |
| VM/OS artifact | `GitLoomOS`, `gitloomos-release`, `build/gitloomos/` | Rename with Phase 3 (build-side only strings) except the *installed* marker files, which follow the distro re-import. |
| Daemon | `gitloomd`, `gitloomd.service`, `/opt/gitloom`, `/home/gitloom` | New unit + binary name in the payload; upgrade path stops/disables the old unit. The CI payload smoke (uid 1000, `HOME=/home/gitloom`) must be updated in lockstep — it asserts these exact strings. |
| Registry | `gitloom://` protocol, "Open in GitLoom" context menu, uninstall key, App Paths | Installer writes new keys + deletes old on upgrade; keep `gitloom://` **also** registered for one release if any docs/emails linked it, else cut clean. |
| Single-instance mutex / pipe names | `GitLoom.*` | Rename freely (process-lifetime only) — but old+new app must not run concurrently during upgrade; the installer already enforces single-instance. |
| Keyring entries | DPAPI key ring under the data root; keys `token_<host>`, `sshpass_<keypath>` | Ride along with the data-root move (same directory). Service-name strings if any → migrate-read. |
| SQLite DB file | under data root | Rides the data-root move; no schema impact. |
| Docker images | `gitloom-agent-base`, `gitloom-egress-proxy`, `gitloom-dev` | Rename in `images/`, CI, and the sandbox launcher together; per-repo persistent jails re-create on next provision (document that agents' warm state resets once). |
| Adapter/jail labels, audit log identity strings | various `gitloom` prefixes | Rename at Phase 4 end; the hash-chained audit log (P2-15) records identity — land the rename **before** the first tamper-evident release so the chain never contains a mid-stream identity flip. |
| Velopack app id / Setup.exe name | `build/velopack/pack.ps1` | New app id = new install lineage. Ship the rename in a version that the *old* feed serves as its final update, pointing at the new feed (standard Velopack migration), or accept a clean reinstall at alpha. |

## Phase 5 — external services & cutover

- **Domains:** point `mainguard.dev` at the site (Pages custom domain + CNAME), `mainguard.ai`
  redirect. Update `og:url`, sitemap, any published links (README badges, GTM docs).
- **Cloudflare Worker:** deploy under `mainguard-site-api` (new name = new URL) with the same D1
  binding; flip `API_BASE` in `site/src/config.ts`; keep the old worker route alive two weeks,
  then delete. D1 database name can stay (`gitloom-site`) — it is invisible — or export/import
  once traffic is on the new worker.
- **Waitlist wire id:** site switches `weave` → `cloud` (worker already accepts both since
  Phase 0); optionally backfill stored interests with one D1 UPDATE.
- **`og.png` + crafted logo:** regenerate from the final SVG; favicon already carries the interim
  M-gatehouse.
- **Resend sender / email templates**, Turnstile widget hostname allowlist (add `mainguard.dev`),
  worker `ALLOWED_ORIGINS` (add the new origins in the same deploy as the domain cutover).
- Trademark filing + `NOTICE`/license headers if any name the product.

## Cross-cutting cautions

- **In-flight work:** 60 phase-2 branches plan against `GitLoom.*` paths; the master doc's
  reviewer scripts quote them literally. Phase 3 must either land in a merge-freeze window or
  the master doc gets a path-alias preamble ("`GitLoom.*` ≙ `Mainguard.*` post-rebrand") first.
- **Memory/docs debt:** `docs/phase-2/**` task specs, ADRs, and GTM materials say GitLoom
  thousands of times. Prose renames ride Phase 1; *normative* strings inside reviewer commands
  ride Phase 3 (see above). Don't chase 100% — historical documents (ADRs, dated market
  research) may keep the old name with a banner note.
- **The final grep:** each phase ends with
  `grep -ri gitloom --include=… .` against an explicit allowlist file (`docs/rebrand/allowlist.txt`)
  that shrinks every phase; CI can enforce it from Phase 3 on.

## Suggested order & effort

| Phase | Size | Risk | When |
|---|---|---|---|
| 0 — docs + site | done | low | now |
| 1 — product strings | ~1 day | low | next |
| 2 — repo rename | ~½ day | low | with owner, after 1 |
| 3 — code identifiers | 1–2 days + freeze | medium (CI greps, EF, protos) | quiet window after audit series merges |
| 4 — persisted identifiers | 2–4 days | high (WSL distro, daemon, installer upgrade) | before first public alpha installer |
| 5 — external cutover | ~1 day | low-medium | when domains are live |
