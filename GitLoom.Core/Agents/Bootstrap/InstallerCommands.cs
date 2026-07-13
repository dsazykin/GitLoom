using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// The exact, enumerated privileged surface of the P2-21 elevated helper. The helper does <b>only</b>
/// these two things — enable the two Windows optional features, and register the elevated resume
/// Scheduled Task — and nothing else ever moves into it (rejection trigger otherwise, plan §7). Every
/// command is a pure argument-list/script builder so the shapes are unit-testable without a process,
/// and the raw PowerShell is surfaced to the user in the OOBE before the single UAC prompt.
/// </summary>
public static class InstallerCommands
{
    /// <summary>The two Windows optional features WSL2 requires.</summary>
    public static readonly IReadOnlyList<string> RequiredFeatures = new[]
    {
        "Microsoft-Windows-Subsystem-Linux",
        "VirtualMachinePlatform",
    };

    /// <summary>The name of the elevated resume Scheduled Task. Scoped, self-deleting after resume.</summary>
    public const string ResumeTaskName = "GitLoom-OOBE-Resume";

    /// <summary>
    /// The raw <c>Enable-WindowsOptionalFeature</c> PowerShell surfaced to the user (and run by the
    /// elevated helper). <c>-NoRestart</c> so the OOBE — not DISM — owns the reboot decision. This
    /// exact string is what the OOBE displays before the UAC prompt (transparency).
    /// </summary>
    public static string EnableFeaturesPowerShell()
    {
        // One Enable-WindowsOptionalFeature call per feature, -NoRestart so we control the reboot.
        var lines = new List<string>();
        foreach (var feature in RequiredFeatures)
            lines.Add($"Enable-WindowsOptionalFeature -Online -FeatureName {feature} -NoRestart -All");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The <c>schtasks.exe</c> argument list that registers the elevated, ONLOGON, run-as-highest
    /// resume task. <b>Never <c>RunOnce</c></b> (plan §7 rejection trigger) — a Scheduled Task
    /// survives the reboot and elevation cleanly where a <c>RunOnce</c> registry value would run
    /// unelevated. <paramref name="resumeExePath"/> is the OOBE exe relaunched in resume mode.
    /// </summary>
    public static IReadOnlyList<string> RegisterResumeTask(string resumeExePath) => new[]
    {
        "/Create",
        "/TN", ResumeTaskName,
        // The task runs the OOBE exe in resume mode.
        "/TR", $"\"{resumeExePath}\" --resume",
        "/SC", "ONLOGON",
        "/RL", "HIGHEST",   // elevated — feature-enablement completion + VM import need it
        "/F",               // overwrite any stale registration (idempotent re-run)
    };

    /// <summary>The <c>schtasks.exe</c> argument list that deletes the resume task — the helper/OOBE
    /// runs this once the resume completes, so the task is self-deleting (never lingers).</summary>
    public static IReadOnlyList<string> UnregisterResumeTask() => new[]
    {
        "/Delete",
        "/TN", ResumeTaskName,
        "/F",
    };

    /// <summary>The two enumerated privileged actions, so a test can prove the helper's scope never
    /// grows: exactly {enable features, register resume task}.</summary>
    public static IReadOnlyList<string> PrivilegedActionCatalog() => new[]
    {
        "enable-windows-optional-features",
        "register-resume-scheduled-task",
    };
}

/// <summary>How the elevated helper reports its outcome to the unelevated OOBE: a process exit code
/// PLUS a JSON result file (so structured detail survives the elevation boundary).</summary>
public enum ElevatedHelperExitCode
{
    Success = 0,
    /// <summary>Enabling one or both features failed.</summary>
    FeatureEnableFailed = 10,
    /// <summary>Registering the resume Scheduled Task failed.</summary>
    ResumeTaskRegistrationFailed = 11,
    /// <summary>The helper was not actually running elevated.</summary>
    NotElevated = 12,
    /// <summary>Malformed/absent arguments.</summary>
    BadArguments = 13,
}

/// <summary>The JSON result file the elevated helper writes for the OOBE to read back.</summary>
public sealed record ElevatedHelperResult
{
    public required bool FeaturesEnabled { get; init; }
    public required bool RebootRequired { get; init; }
    public required bool ResumeTaskRegistered { get; init; }
    public string? Error { get; init; }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static ElevatedHelperResult Deserialize(string json) =>
        JsonSerializer.Deserialize<ElevatedHelperResult>(json, Options)
        ?? throw new System.IO.InvalidDataException("elevated helper result deserialized to null.");
}
