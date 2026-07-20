# Mainguard packaging (Velopack) — two edition channels

This is how Mainguard ships as **self-updating Windows executables**. Phase 3 (packaging lanes) turns the
single Velopack pipeline into **two channels built from one commit**, selected by `-Channel`:

| Channel  | Head published            | Main exe                    | GitLoomOS payload / OOBE | Install size |
|----------|---------------------------|-----------------------------|--------------------------|--------------|
| `client` | `Mainguard.Client.App`    | `Mainguard.Client.App.exe`  | **NO** (agent-platform-free) | small / fast |
| `pro`    | `Mainguard.Pro.App`       | `Mainguard.Pro.App.exe`     | **YES** (VM + daemon + OOBE + elevated helper) | full |

The **client** head references `Mainguard.App.Shell` **only**, so its published closure physically excludes
the whole agent platform (`Mainguard.Agents(.UI)` / `Mainguard.Protos` / `Docker.DotNet` / `Porta.Pty` /
`Grpc`) — enforced by `build/ci/verify-client-closure.sh`. It has no OOBE, so it ships **no** GitLoomOS
payload and **no** elevated helper: a small, quick install. The **pro** head references `Mainguard.Agents.UI`
and carries the GitLoomOS payload-bundling MSBuild targets (moved onto it in step 2f), so it is the full
self-provisioning installer.

Each channel publishes to its own `artifacts/publish/<...>` dir and packs to its own
`artifacts/releases/<channel>` RELEASES feed, so the two never collide.

## The pipeline

```
                                              build/gitloomos/build.sh ──► GitLoomOS.tar.gz  (pro only;
                                                        │                   CI job: payload-reproducible)
                                                        ▼  (/p:GitLoomOsPayload=<path>)
 -Channel pro    → dotnet publish Mainguard.Pro.App    ──► publish dir ── 2f MSBuild targets co-locate:
                                                        │                   • Mainguard.Installer.Elevated.exe (single-UAC helper)
                                                        │                   • payload/GitLoomOS.tar.gz          (imported by OOBE)
                                                        │                   • payload/daemon, payload/images    (daemon + jail images)
                                                        ▼
                        build/velopack/pack.ps1 -Channel pro    (vpk pack) ──► artifacts/releases/pro/    (Setup.exe + delta feed)


 -Channel client → dotnet publish Mainguard.Client.App ──► publish dir  (NO payload, NO helper — closure is agent-platform-free)
                                                        ▼
                        build/velopack/pack.ps1 -Channel client (vpk pack) ──► artifacts/releases/client/ (Setup.exe + delta feed)
```

The Pro co-location is enforced by the MSBuild targets in `Mainguard.Pro.App/Mainguard.Pro.App.csproj`
(`CopyElevatedHelperToPublish`, `BundleGitLoomOsPayloadToPublish`, plus the daemon / jail-image bundlers).
`pack.ps1` re-asserts the elevated helper + `payload/GitLoomOS.tar.gz` exist before packing the **pro**
channel, so a "file not found" elevation/import gap can never ship. The **client** channel skips that check
by design (there is nothing to co-locate).

## Prerequisites (owner's Windows release box)

1. **Velopack tool:** `dotnet tool install -g vpk`
2. **(pro only) GitLoomOS payload:** run `build/gitloomos/build.sh` (WSL/Linux + Docker) or download the CI
   `payload-reproducible` artifact. Pass its path as `-PayloadPath`.
3. **(Optional but recommended) code-signing cert** — see below.

## Build

```powershell
# Full Pro installer (with the bundled VM payload):
pwsh build/velopack/pack.ps1 -Channel pro    -Version 0.2.5 -PayloadPath C:\artifacts\GitLoomOS.tar.gz

# Small free-client installer (no payload):
pwsh build/velopack/pack.ps1 -Channel client -Version 0.2.5
```

Output per channel: `artifacts/releases/<channel>/` containing the channel's `*-Setup.exe`, the full/delta
`.nupkg`, and the `RELEASES` feed the app self-updates from (`VelopackApp` + `UpdateManager`, pointed at that
channel's release URL).

To verify the channel wiring where `vpk`/the payload are absent (e.g. in CI or a Linux dev box), pass
`-DryRun`: the script resolves the full per-channel plan and prints the exact `dotnet publish` + `vpk pack`
commands it *would* run, executing nothing.

## `packId` / app-id — a **Phase-4** owner call (not decided here)

`pack.ps1` exposes **`-PackId`, `-PackTitle`, `-PackAuthors` as parameters** with per-channel interim
defaults. The Velopack `packId` is the **update-lineage identity**: a shipped install only self-updates from a
RELEASES feed carrying the **same** `packId`, so changing it forks the install base.

- Phase 3 (this change) **deliberately does not rename** the existing persisted `GitLoom` `packId` to
  `Mainguard`. Whether the **pro** channel inherits the existing GitLoom install base (keep `packId
  "GitLoom"`) or starts a fresh Mainguard lineage — together with migrating the other persisted `GitLoom`
  identifiers (`GitLoomEnv`, `gitloomd`, `GitLoomOS.tar.gz`, `%LocalAppData%\GitLoom`, the **"GitLoom Setup"**
  UAC string) — is a **Phase-4 business decision**, not a packaging-structure change.
- The interim defaults therefore stay in the persisted `GitLoom` namespace: **pro → `GitLoom`** (the existing
  lineage, untouched), **client → `GitLoomClient`** (a brand-new artifact with no install base, hence its own
  distinct id). Human-facing `-PackTitle`/`-PackAuthors` follow the already-shipped Mainguard rebrand
  (`Mainguard Pro` / `Mainguard`).
- **All of these are overridable** — the owner sets the final ids/titles at release time, e.g.
  `pack.ps1 -Channel pro -PackId Mainguard -PackTitle "Mainguard"` once Phase 4 lands the lineage decision.

## Code-signing hook (verified publisher in UAC)

Without a signed exe, the UAC prompt shows an **unknown publisher**. With the owner's cert it shows a
**verified publisher** and the branded name **"GitLoom Setup"** (the elevated helper's version-resource
`FileDescription` / manifest description — set in `installer/Mainguard.Installer.Elevated/*` +
`Mainguard.App.Shell/app.manifest`; the string itself is a Phase-4 rename item). No cert is invented here;
wire yours in either place:

- **At publish (pro)** — the `SignGitLoomExecutables` target in `Mainguard.Pro.App.csproj` signs
  `Mainguard.Pro.App.exe` and `Mainguard.Installer.Elevated.exe` when `$(GitLoomSigningCertPath)` is set:
  ```powershell
  dotnet publish Mainguard.Pro.App/Mainguard.Pro.App.csproj ... /p:GitLoomSigningCertPath=C:\certs\mainguard.pfx /p:GitLoomSigningCertPassword=***
  ```
- **At pack (both channels)** — pass `-SigningCertPath`/`-SigningCertPassword` (or the `GITLOOM_SIGNING_CERT_*`
  env vars) to `pack.ps1`; it forwards `--signParams` to `vpk` so the channel's Setup.exe and shim are signed
  too.

## The one wiring step that stays OFF until the package ships

To make a published exe self-update, add the Velopack bootstrap as the **first** statement of the head's
`Program.Main` (before the shim handling), and the package reference to that head's `.csproj`:

```csharp
// Mainguard.Client.App/Program.cs or Mainguard.Pro.App/Program.cs — first statement in Main:
Velopack.VelopackApp.Build().Run();
```
```xml
<!-- Mainguard.Client.App.csproj / Mainguard.Pro.App.csproj -->
<PackageReference Include="Velopack" Version="0.0.*" />
```

This is intentionally **not committed** yet: it requires a NuGet restore of the Velopack package and a
release feed URL, neither of which is verifiable in the build sandbox. It is the single documented step the
owner enables on the release box. Everything else (co-location, payload bundling, the branded/windowless
helper, the in-app OOBE, launch routing, the two-channel split) is committed and build-verified.

## What is verified vs. owner-matrix

| Piece | Status |
|---|---|
| Both channels publish (client + pro), self-contained win-x64 | **build-verified** (`package-smoke` CI + local publish) |
| Client head closure is FREE of the agent platform | **build-verified** (`build/ci/verify-client-closure.sh`, CI `client-closure`) |
| Pro: elevated helper + GitLoomOS payload co-located in publish | **build-verified** (MSBuild targets + `package-smoke` CI) |
| Windowless (WinExe) installer + helper, hidden child processes | **build-verified** (csproj `OutputType`, code) |
| Branded UAC name resource + manifest | **build-verified** (compiles into the version resource) |
| In-app OOBE wizard (5 themes) + launch routing (Pro) | **build+test-verified** (render harness PNGs + unit tests) |
| `vpk pack` producing Setup.exe, self-update, real UAC branding, signed publisher | **owner Windows matrix** (needs vpk tool + cert + payload artifact) |
