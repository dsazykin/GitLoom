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
               NO MainguardOS payload, NO OOBE/elevated-helper - a small, fast install. Emitted to its own feed.

      pro    -> dotnet publish Mainguard.Pro.App (self-contained win-x64), --mainExe Mainguard.Pro.App.exe.
               The Pro head references Mainguard.Agents.UI and carries the MainguardOS payload-bundling MSBuild
               targets (moved onto it in step 2f): it co-locates the elevated helper
               (Mainguard.Installer.Elevated.exe) and bundles payload/MainguardOS.tar.gz (+ daemon / jail-image
               payloads), so first-run provisioning + the in-app OOBE work with no command line. Emitted to its
               own feed. This is the full installer.

    Each channel publishes to its own artifacts/publish/<...> dir and packs to its own
    artifacts/releases/<channel> RELEASES feed, so the two never collide.

    Nothing here is a stub, but a full `vpk pack` requires inputs that live outside this sandbox and are
    supplied by the owner's Windows release box:
      1. The `vpk` tool:        dotnet tool install -g vpk
      2. (pro only) MainguardOS payload: build/mainguardos/build.sh output (or the CI `payload-reproducible` artifact)
      3. (optional) a signing cert so UAC/SmartScreen show a VERIFIED publisher instead of unknown.

    -DryRun resolves the full per-channel plan and prints the exact `dotnet publish` + `vpk pack` commands
    WITHOUT running anything - useful for verifying the channel wiring where `vpk`/the payload are absent.

.EXAMPLE
    # Full Pro installer (with the bundled VM payload + signing):
    pwsh build/velopack/pack.ps1 -Channel pro -Version 0.2.5 `
        -PayloadPath C:\artifacts\MainguardOS.tar.gz `
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
    # PHASE-4 DECISION (owner, 2026-07-20) - packId RENAMED to the new Mainguard lineage; clean reinstall.
    # -----------------------------------------------------------------------------------------
    # The Velopack packId is the UPDATE-LINEAGE identity: every shipped install self-updates from
    # the RELEASES feed that carries the SAME packId, so changing it forks the install base.
    #
    #   * The old shipped packId was "GitLoom" (pro) / "GitLoomClient" (client). The owner chose a
    #     NEW Mainguard packId with NO lineage bridge: pro -> "Mainguard", client -> "MainguardClient".
    #     A clean reinstall for the (tiny, alpha) existing GitLoom install base is ACCEPTED - the old
    #     "GitLoom" feed does NOT ship a final cross-lineage update pointing here. The user's data
    #     (repos/prefs/keyring) still survives independently via the %LocalAppData%\GitLoom ->
    #     \Mainguard data-root move (MainguardPaths.MigrateLegacyWindowsDataRootOnce); only the
    #     self-update lineage resets.
    #   * This lands in step with the rest of the Phase-4 persisted-id rename (MainguardEnv /
    #     mainguardd / MainguardOS.tar.gz / %LocalAppData%\Mainguard / the "Mainguard Setup" UAC string).
    #   * ALL of these are -Param overridable: the owner sets the final ids/titles at release time.
    #     Leave empty to accept the per-channel default resolved below.
    # =========================================================================================
    [string]$PackId = "",
    [string]$PackTitle = "",
    [string]$PackAuthors = "",

    # pro only: the MainguardOS VM tarball the Pro head bundles at payload/MainguardOS.tar.gz. Ignored for client.
    [string]$PayloadPath = "$PSScriptRoot/../mainguardos/out/MainguardOS.tar.gz",

    [string]$SigningCertPath = $env:MAINGUARD_SIGNING_CERT_PATH,
    [string]$SigningCertPassword = $env:MAINGUARD_SIGNING_CERT_PASSWORD,

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
        $defaultPackId  = "MainguardClient"   # NEW Mainguard lineage, no install base (see PHASE-4 DECISION).
        $defaultTitle   = "Mainguard"
        $bundlePayload  = $false            # small/fast install: NO MainguardOS payload, NO OOBE / elevated helper.
    }
    'pro' {
        $projectPath    = Join-Path $repo "Mainguard.Pro.App/Mainguard.Pro.App.csproj"
        $mainExe        = "Mainguard.Pro.App.exe"
        $publishLeaf    = "Mainguard.Pro"
        $feedLeaf       = "pro"
        $defaultPackId  = "Mainguard"         # NEW Mainguard lineage; clean reinstall off old "GitLoom" (see PHASE-4 DECISION).
        $defaultTitle   = "Mainguard Pro"
        $bundlePayload  = $true             # full install: MainguardOS payload + daemon + OOBE + elevated helper.
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
    # Pro only: the MainguardOS payload-bundling target consumes this (daemon / image payloads default in the csproj).
    $publishArgs += "/p:MainguardOsPayload=$PayloadPath"
    # Pro only: the csproj SignMainguardExecutables target signs the app + elevated helper at publish when set.
    if ($SigningCertPath) {
        $publishArgs += "/p:MainguardSigningCertPath=$SigningCertPath"
        $publishArgs += "/p:MainguardSigningCertPassword=$SigningCertPassword"
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
    $payload = Join-Path $publishDir "payload/MainguardOS.tar.gz"
    if ($DryRun) {
        Write-Host "    [dry-run] assert co-located: $helper  +  $payload"
    } else {
        if (-not (Test-Path $helper))  { throw "Elevated helper missing from publish ($helper) - UAC hand-off would fail." }
        if (-not (Test-Path $payload)) { throw "MainguardOS payload missing from publish ($payload) - set -PayloadPath to the CI artifact." }
        Write-Host "==> Co-location OK: elevated helper + MainguardOS payload are in the publish dir."
    }
} else {
    Write-Host "==> [$Channel] Small install: no MainguardOS payload / elevated helper expected (client head is agent-platform-free)."
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
