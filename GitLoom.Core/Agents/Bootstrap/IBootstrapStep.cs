using System;
using System.Threading;
using System.Threading.Tasks;

namespace GitLoom.Core.Agents.Bootstrap;

/// <summary>
/// One ordered stage of the P2-05 bootstrap state machine. Each step is a check/act pair: the
/// bootstrapper <see cref="IsSatisfiedAsync"/>-checks first and only <see cref="ExecuteAsync"/>-acts
/// when reality is not yet what the step wants — the property that makes the whole run idempotent and
/// resumable after a mid-bootstrap kill.
/// </summary>
public interface IBootstrapStep
{
    /// <summary>The checklist label shown in the progress UI.</summary>
    string Name { get; }

    /// <summary>Check phase: is the world already in this step's desired state? MUST NOT mutate.</summary>
    Task<bool> IsSatisfiedAsync(CancellationToken ct);

    /// <summary>Act phase: bring the world into this step's desired state, streaming log lines.</summary>
    Task ExecuteAsync(IProgress<string> log, CancellationToken ct);
}

/// <summary>The lifecycle state of a bootstrap stage, mirrored by the progress UI.</summary>
public enum BootstrapStageState
{
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>A progress update the bootstrapper reports for a stage (state transition and/or log tail).</summary>
public sealed record BootstrapProgress(string StepName, BootstrapStageState State, string? Log);

/// <summary>Inputs to the bootstrapper: the distro to provision and where its tarball/install dir live.</summary>
public sealed record BootstrapOptions(string InstallDir, string TarballPath, string DistroName = WslCommands.DistroName);

/// <summary>
/// Thin filesystem seam for the <c>.wslconfig</c> merge/backup path and host RAM detection, so
/// <see cref="WslConfigMergeStep"/> is unit-testable without touching <c>%UserProfile%</c>.
/// </summary>
public interface IBootstrapFileSystem
{
    /// <summary>Absolute path to <c>%UserProfile%\.wslconfig</c>.</summary>
    string WslConfigPath { get; }

    /// <summary>The current <c>.wslconfig</c> content, or <c>null</c> when the file does not exist.</summary>
    string? ReadWslConfig();

    /// <summary>Writes a timestamped <c>.wslconfig.gitloom.bak</c> next to the file, BEFORE any write.
    /// No-op when the file does not exist. Never clobbers an existing backup (timestamped name).</summary>
    void BackupWslConfig();

    /// <summary>Writes the merged <c>.wslconfig</c> content.</summary>
    void WriteWslConfig(string content);

    /// <summary>Whether an arbitrary file (e.g. the distro tarball) exists.</summary>
    bool FileExists(string path);

    /// <summary>Total physical RAM in bytes, used to compute the <c>memory=</c> default.</summary>
    long TotalPhysicalMemoryBytes { get; }
}

/// <summary>
/// Health-probe seam for <see cref="HealthCheckStep"/>. The App supplies an implementation backed by
/// <c>DaemonClient</c>'s gRPC connection; tests supply a scripted one. Kept in Core so the
/// bootstrapper carries no UI dependency.
/// </summary>
public interface IDaemonHealthProbe
{
    /// <summary>True once the daemon's gRPC surface answers a health probe.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct);
}
