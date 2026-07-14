#Requires -Version 5.1
<#
.SYNOPSIS
    P2-48 — package GitLoom as ONE self-updating Windows executable (Velopack).

.DESCRIPTION
    Produces the single-exe distribution: `dotnet publish` (self-contained win-x64) of GitLoom.App,
    which — via the P2-48 MSBuild targets in GitLoom.App.csproj — co-locates the elevated helper
    (GitLoom.Installer.Elevated.exe) and bundles the GitLoomOS payload at payload/GitLoomOS.tar.gz,
    then `vpk pack` wraps that publish directory into a self-updating installer + release feed.

    Nothing here is a stub, but a full pack requires three inputs that live outside this sandbox and are
    supplied by the owner's Windows release box:
      1. The `vpk` tool:        dotnet tool install -g vpk
      2. The GitLoomOS payload: build/gitloomos/build.sh output (or the CI `payload-reproducible` artifact)
      3. (optional) a signing cert so UAC/SmartScreen show a VERIFIED publisher instead of unknown.

.EXAMPLE
    pwsh build/velopack/pack.ps1 -Version 0.2.0 `
        -PayloadPath C:\artifacts\GitLoomOS.tar.gz `
        -SigningCertPath C:\certs\gitloom.pfx -SigningCertPassword $env:GITLOOM_CERT_PW
#>
param(
    [string]$Version = "0.2.0",
    [string]$Runtime = "win-x64",
    [string]$PayloadPath = "$PSScriptRoot/../gitloomos/out/GitLoomOS.tar.gz",
    [string]$SigningCertPath = $env:GITLOOM_SIGNING_CERT_PATH,
    [string]$SigningCertPassword = $env:GITLOOM_SIGNING_CERT_PASSWORD,
    # A previous release directory so vpk builds a DELTA package for self-update (optional).
    [string]$ReleaseDir = "$PSScriptRoot/../../artifacts/releases"
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$publishDir = Join-Path $repo "artifacts/publish/GitLoom"

Write-Host "==> Publishing GitLoom.App (self-contained $Runtime)..."
$publishArgs = @(
    "publish", (Join-Path $repo "GitLoom.App/GitLoom.App.csproj"),
    "-c", "Release", "-r", $Runtime, "--self-contained", "true",
    "-o", $publishDir,
    "/p:GitLoomOsPayload=$PayloadPath"
)
if ($SigningCertPath) {
    $publishArgs += "/p:GitLoomSigningCertPath=$SigningCertPath"
    $publishArgs += "/p:GitLoomSigningCertPassword=$SigningCertPassword"
}
dotnet @publishArgs

# --- Sanity: the two co-location invariants P2-48 owns must hold before we pack. ---
$helper = Join-Path $publishDir "GitLoom.Installer.Elevated.exe"
$payload = Join-Path $publishDir "payload/GitLoomOS.tar.gz"
if (-not (Test-Path $helper))  { throw "Elevated helper missing from publish ($helper) — UAC hand-off would fail." }
if (-not (Test-Path $payload)) { throw "GitLoomOS payload missing from publish ($payload) — set -PayloadPath to the CI artifact." }
Write-Host "==> Co-location OK: elevated helper + GitLoomOS payload are in the publish dir."

# --- Velopack pack: one self-updating executable. ---
Write-Host "==> Running vpk pack (v$Version)..."
$vpkArgs = @(
    "pack",
    "--packId", "GitLoom",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "GitLoom.App.exe",
    "--packTitle", "GitLoom",
    "--packAuthors", "GitLoom",
    "--outputDir", $ReleaseDir
)
if ($SigningCertPath) {
    # signtool-style signing so the installer + exe show a verified publisher.
    $vpkArgs += @("--signParams", "/fd SHA256 /f `"$SigningCertPath`" /p `"$SigningCertPassword`" /tr http://timestamp.digicert.com /td SHA256")
}
vpk @vpkArgs

Write-Host "==> Done. Self-updating release written to $ReleaseDir (Setup.exe + RELEASES feed)."
