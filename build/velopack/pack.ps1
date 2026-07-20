#Requires -Version 5.1
<#
.SYNOPSIS
    Package Mainguard as a self-updating Windows executable (Velopack) - TWO edition channels.

.DESCRIPTION
    Phase 3 (packaging lanes). From ONE commit this produces two independent Velopack channels,
    selected by -Channel:

      client -> dotnet publish Mainguard.Client.App (self-contained win-x64), --mainExe
               Mainguard.Client.App.exe. The plain Git-client head references Mainguard.App.Shell ONLY,
               so its closure is FREE of the agent platform (enforced by build/ci/verify-client-closure.sh).
               NO GitLoomOS payload, NO OOBE/elevated-helper - a small, fast install. Emitted to its own feed.

      pro    -> dotnet publish Mainguard.Pro.App (self-contained win-x64), --mainExe Mainguard.Pro.App.exe.
               The Pro head references Mainguard.Agents.UI and carries the GitLoomOS payload-bundling MSBuild
               targets (moved onto it in step 2f): it co-locates the elevated helper
               (Mainguard.Installer.Elevated.exe) and bundles payload/GitLoomOS.tar.gz (+ daemon / jail-image
               payloads), so first-run provisioning + the in-app OOBE work with no command line. Emitted to its
               own feed. This is the full installer.

    Each channel publishes to its own artifacts/publish/<...> dir and packs to its own
    artifacts/releases/<channel> RELEASES feed, so the two never collide.

    Nothing here is a stub, but a full `vpk pack` requires inputs that live outside this sandbox and are
    supplied by the owner's Windows release box:
      1. The `vpk` tool:        dotnet tool install -g vpk
      2. (pro only) GitLoomOS payload: build/gitloomos/build.sh output (or the CI `payload-reproducible` artifact)
      3. (optional) a signing cert so UAC/SmartScreen show a VERIFIED publisher instead of unknown.

    -DryRun resolves the full per-channel plan and prints the exact `dotnet publish` + `vpk pack` commands
    WITHOUT running anything - useful for verifying the channel wiring where `vpk`/the payload are absent.

.EXAMPLE
    # Full Pro installer (with the bundled VM payload + signing):
    pwsh build/velopack/pack.ps1 -Channel pro -Version 0.2.5 `
        -PayloadPath C:\artifacts\GitLoomOS.tar.gz `
        -SigningCertPath C:\certs\mainguard.pfx -SigningCertPassword $env:MAINGUARD_CERT_PW

.EXAMPLE
    # Small free-client installer (no payload):
    pwsh build/velopack/pack.ps1 -Channel client -Version 0.2.5

.EXAMPLE
    # Verify the channel wiring without vpk / a payload:
    pwsh build/velopack/pack.ps1 -Channel pro -DryRun
#>
param(
    # Which edition channel to build. Distinct output dirs / RELEASES feeds keep the two independent.
    [ValidateSet('client', 'pro')]
    [string]$Channel = 'pro',

    [string]$Version = "0.2.0",
    [string]$Runtime = "win-x64",

    # =========================================================================================
    # PHASE-4 NOTE - packId / app-id are PARAMETERS here, NOT a persisted-id rename.
    # -----------------------------------------------------------------------------------------
    # The Velopack packId is the UPDATE-LINEAGE identity: every shipped install self-updates from
    # the RELEASES feed that carries the SAME packId, so changing it forks the install base.
    #
    #   * The shipped/persisted packId today is "GitLoom". Phase 3 (this change) DELIBERATELY does
    #     NOT rename it to "Mainguard". Whether the Pro channel INHERITS the existing GitLoom
    #     install base (keep packId "GitLoom") or starts a fresh Mainguard lineage - and the
    #     matching migration of the persisted "GitLoom" identifiers (GitLoomEnv / gitloomd /
    #     GitLoomOS.tar.gz / %LocalAppData%\GitLoom / the "GitLoom Setup" UAC string) - is a
    #     PHASE-4 owner business call, not a packaging-structure change.
    #   * So the interim DEFAULTS below keep the packId in the persisted "GitLoom" namespace:
    #     pro  -> "GitLoom" (the existing lineage, untouched), client -> "GitLoomClient" (a NEW
    #     artifact with no install base, hence its own distinct id). Human-facing title/authors
    #     follow the already-shipped Mainguard rebrand.
    #   * ALL of these are -Param overridable: the owner sets the final ids/titles at release time.
    #     Leave empty to accept the per-channel interim default resolved below.
    # =========================================================================================
    [string]$PackId = "",
    [string]$PackTitle = "",
    [string]$PackAuthors = "",

    # pro only: the GitLoomOS VM tarball the Pro head bundles at payload/GitLoomOS.tar.gz. Ignored for client.
    [string]$PayloadPath = "$PSScriptRoot/../gitloomos/out/GitLoomOS.tar.gz",

    [string]$SigningCertPath = $env:GITLOOM_SIGNING_CERT_PATH,
    [string]$SigningCertPassword = $env:GITLOOM_SIGNING_CERT_PASSWORD,

    # The channel's RELEASES feed / delta root. Empty => artifacts/releases/<channel> (distinct per channel).
    [string]$ReleaseDir = "",

    # Resolve + print the plan (publish + pack commands) but run nothing. For wiring verification.
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot/../..").Path

# ---- Resolve per-channel configuration (project head, main exe, publish/feed dirs, payload policy). ----
switch ($Channel) {
    'client' {
        $projectPath    = Join-Path $repo "Mainguard.Client.App/Mainguard.Client.App.csproj"
        $mainExe        = "Mainguard.Client.App.exe"
        $publishLeaf    = "Mainguard.Client"
        $feedLeaf       = "client"
        $defaultPackId  = "GitLoomClient"   # NEW artifact, no install base - distinct interim id (see PHASE-4 NOTE).
        $defaultTitle   = "Mainguard"
        $bundlePayload  = $false            # small/fast install: NO GitLoomOS payload, NO OOBE / elevated helper.
    }
    'pro' {
        $projectPath    = Join-Path $repo "Mainguard.Pro.App/Mainguard.Pro.App.csproj"
        $mainExe        = "Mainguard.Pro.App.exe"
        $publishLeaf    = "Mainguard.Pro"
        $feedLeaf       = "pro"
        $defaultPackId  = "GitLoom"         # EXISTING persisted lineage - LEFT untouched for Phase 4 (see PHASE-4 NOTE).
        $defaultTitle   = "Mainguard Pro"
        $bundlePayload  = $true             # full install: GitLoomOS payload + daemon + OOBE + elevated helper.
    }
}

# Apply interim defaults only where the owner didn't override.
if (-not $PackId)      { $PackId = $defaultPackId }
if (-not $PackTitle)   { $PackTitle = $defaultTitle }
if (-not $PackAuthors) { $PackAuthors = "Mainguard" }
if (-not $ReleaseDir)  { $ReleaseDir = Join-Path $repo "artifacts/releases/$feedLeaf" }

$publishDir = Join-Path $repo "artifacts/publish/$publishLeaf"

# ---- Publish the selected head (self-contained win-x64), same flags as the single-channel script used. ----
Write-Host "==> [$Channel] Publishing $projectPath (self-contained $Runtime) -> $publishDir"
$publishArgs = @(
    "publish", $projectPath,
    "-c", "Release", "-r", $Runtime, "--self-contained", "true",
    "-o", $publishDir
)
if ($bundlePayload) {
    # Pro only: the GitLoomOS payload-bundling target consumes this (daemon / image payloads default in the csproj).
    $publishArgs += "/p:GitLoomOsPayload=$PayloadPath"
    # Pro only: the csproj SignGitLoomExecutables target signs the app + elevated helper at publish when set.
    if ($SigningCertPath) {
        $publishArgs += "/p:GitLoomSigningCertPath=$SigningCertPath"
        $publishArgs += "/p:GitLoomSigningCertPassword=$SigningCertPassword"
    }
}
if ($DryRun) {
    Write-Host "    [dry-run] dotnet $($publishArgs -join ' ')"
} else {
    dotnet @publishArgs
}

# ---- Sanity: the co-location invariants the Pro head owns must hold before we pack. (client has none.) ----
if ($bundlePayload) {
    $helper  = Join-Path $publishDir "Mainguard.Installer.Elevated.exe"
    $payload = Join-Path $publishDir "payload/GitLoomOS.tar.gz"
    if ($DryRun) {
        Write-Host "    [dry-run] assert co-located: $helper  +  $payload"
    } else {
        if (-not (Test-Path $helper))  { throw "Elevated helper missing from publish ($helper) - UAC hand-off would fail." }
        if (-not (Test-Path $payload)) { throw "GitLoomOS payload missing from publish ($payload) - set -PayloadPath to the CI artifact." }
        Write-Host "==> Co-location OK: elevated helper + GitLoomOS payload are in the publish dir."
    }
} else {
    Write-Host "==> [$Channel] Small install: no GitLoomOS payload / elevated helper expected (client head is agent-platform-free)."
}

# ---- Velopack pack: one self-updating executable, into this channel's own feed. ----
Write-Host "==> [$Channel] vpk pack packId=$PackId v$Version -> $ReleaseDir"
$vpkArgs = @(
    "pack",
    "--packId", $PackId,
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", $mainExe,
    "--packTitle", $PackTitle,
    "--packAuthors", $PackAuthors,
    "--outputDir", $ReleaseDir
)
if ($SigningCertPath) {
    # signtool-style signing so the installer + exe show a verified publisher.
    $vpkArgs += @("--signParams", "/fd SHA256 /f `"$SigningCertPath`" /p `"$SigningCertPassword`" /tr http://timestamp.digicert.com /td SHA256")
}
if ($DryRun) {
    Write-Host "    [dry-run] vpk $($vpkArgs -join ' ')"
    Write-Host "==> [dry-run] Plan resolved for channel '$Channel'. Nothing was executed."
    return
}
vpk @vpkArgs

Write-Host "==> [$Channel] Done. Self-updating release written to $ReleaseDir (Setup.exe + RELEASES feed)."
