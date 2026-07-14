# GitLoom single-exe packaging (Velopack) — P2-48

This is how GitLoom ships as **one self-updating Windows executable** — no console, no `dotnet run`, no
separate installer command. Double-click one file on a fresh Win11 machine → the in-app OOBE wizard
provisions the runtime → the control center opens, with no command line at any point.

## The pipeline

```
build/gitloomos/build.sh ──► GitLoomOS.tar.gz  (reproducible payload; CI job: payload-reproducible)
                                   │
                                   ▼  (/p:GitLoomOsPayload=<path>)
dotnet publish GitLoom.App  ──► publish dir  ── P2-48 MSBuild targets co-locate:
                                   │              • GitLoom.Installer.Elevated.exe  (single-UAC helper)
                                   │              • payload/GitLoomOS.tar.gz          (imported by OOBE)
                                   ▼
build/velopack/pack.ps1  (vpk pack) ──► Setup.exe + delta feed  (self-update)
```

The co-location is enforced by the MSBuild targets in `GitLoom.App/GitLoom.App.csproj`
(`CopyElevatedHelperToPublish`, `BundleGitLoomOsPayloadToPublish`). `pack.ps1` re-asserts both exist
before packing, so a "file not found" elevation/import gap can never ship.

## Prerequisites (owner's Windows release box)

1. **Velopack tool:** `dotnet tool install -g vpk`
2. **GitLoomOS payload:** run `build/gitloomos/build.sh` (WSL/Linux + Docker) or download the CI
   `payload-reproducible` artifact. Pass its path as `-PayloadPath`.
3. **(Optional but recommended) code-signing cert** — see below.

## Build

```powershell
pwsh build/velopack/pack.ps1 -Version 0.2.0 -PayloadPath C:\artifacts\GitLoomOS.tar.gz
```

Output: `artifacts/releases/` containing `GitLoom-Setup.exe`, the full/delta `.nupkg`, and the
`RELEASES` feed the app self-updates from (`VelopackApp` + `UpdateManager`, pointed at your release URL).

## Code-signing hook (verified publisher in UAC)

Without a signed exe, the UAC prompt shows an **unknown publisher**. With the owner's cert it shows a
**verified publisher** and the branded name **"GitLoom Setup"** (the elevated helper's version-resource
`FileDescription` / manifest description — already set in `GitLoom.Installer.Elevated.csproj` +
`app.manifest`). No cert is invented here; wire yours in either place:

- **At publish** — the `SignGitLoomExecutables` target in `GitLoom.App.csproj` signs `GitLoom.App.exe`
  and `GitLoom.Installer.Elevated.exe` when `$(GitLoomSigningCertPath)` is set:
  ```powershell
  dotnet publish ... /p:GitLoomSigningCertPath=C:\certs\gitloom.pfx /p:GitLoomSigningCertPassword=***
  ```
- **At pack** — pass `-SigningCertPath`/`-SigningCertPassword` (or the `GITLOOM_SIGNING_CERT_*` env vars)
  to `pack.ps1`; it forwards `--signParams` to `vpk` so the Setup.exe and shim are signed too.

## The one wiring step that stays OFF until the package ships

To make the published exe self-update, add the Velopack bootstrap as the **first** line of
`GitLoom.App/Program.cs::Main` and the package reference to `GitLoom.App.csproj`:

```csharp
// GitLoom.App/Program.cs — first statement in Main, before any Avalonia init:
Velopack.VelopackApp.Build().Run();
```
```xml
<!-- GitLoom.App.csproj -->
<PackageReference Include="Velopack" Version="0.0.*" />
```

This is intentionally **not committed** yet: it requires a NuGet restore of the Velopack package and a
release feed URL, neither of which is verifiable in the build sandbox. It is the single documented step
the owner enables on the release box. Everything else in P2-48 (co-location, payload bundling, the
branded/windowless helper, the in-app OOBE, launch routing) is committed and build-verified.

## What is verified vs. owner-matrix

| Piece | Status |
|---|---|
| Elevated helper + payload co-located in publish | **build-verified** (MSBuild targets + `package-smoke` CI) |
| Windowless (WinExe) installer + helper, hidden child processes | **build-verified** (csproj `OutputType`, code) |
| Branded UAC name resource + manifest | **build-verified** (compiles into the version resource) |
| In-app OOBE wizard (5 themes) + launch routing | **build+test-verified** (render harness PNGs + unit tests) |
| `vpk pack` producing Setup.exe, self-update, real UAC branding, signed publisher | **owner Windows matrix** (needs vpk tool + cert + payload artifact) |
