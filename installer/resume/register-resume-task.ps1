<#
.SYNOPSIS
  Registers the P2-21 elevated reboot-resume Scheduled Task (an elevated ONLOGON task — never a
  one-shot registry autostart).

.DESCRIPTION
  After the OOBE enables the two Windows optional features (which require a reboot to finish), setup
  must continue automatically after the restart — WITH elevation, so the VM import and feature-completion
  can run. We use an elevated ONLOGON Scheduled Task rather than a one-shot registry autostart because:
    * a one-shot registry autostart runs UNELEVATED, so it cannot complete the privileged post-reboot
      work; and
    * a Scheduled Task survives the reboot cleanly and self-deletes once setup reaches Done.

  This script is the canonical registration the elevated helper performs
  (GitLoom.Core InstallerCommands.RegisterResumeTask). It must be run elevated.

  G-12: this script never issues the VM-wide WSL shutdown verb; it only registers/unregisters our task.

.PARAMETER ResumeExePath
  Full path to GitLoom.Installer.exe, relaunched with --resume by the task.
#>
param(
    [Parameter(Mandatory = $true)]
    [string] $ResumeExePath
)

$ErrorActionPreference = 'Stop'
$TaskName = 'GitLoom-OOBE-Resume'

# ONLOGON trigger so it fires after the reboot's next logon; RunLevel Highest = elevated.
$action    = New-ScheduledTaskAction -Execute $ResumeExePath -Argument '--resume'
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId (whoami) -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Registered elevated resume task '$TaskName' → $ResumeExePath --resume"
