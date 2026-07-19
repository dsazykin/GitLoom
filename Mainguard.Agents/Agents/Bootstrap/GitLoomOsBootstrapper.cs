using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Exceptions;

namespace Mainguard.Agents.Agents.Bootstrap;

/// <summary>Bundles the seams the default step chain needs, so callers wire them once.</summary>
public sealed record BootstrapContext(
    IWslRunner Wsl,
    IBootstrapFileSystem FileSystem,
    IDaemonHealthProbe HealthProbe,
    BootstrapOptions Options);

/// <summary>
/// The P2-05 client-side state machine that takes a cold (WSL2-enabled) Windows machine to a running,
/// health-checked <c>gitloomd</c>. It runs an ordered chain of <see cref="IBootstrapStep"/>s, each a
/// check/act pair:
/// <list type="bullet">
///   <item>A step whose <c>IsSatisfiedAsync</c> is already true is <b>skipped</b> — an all-satisfied
///   run performs zero acts (the re-run-is-a-no-op invariant).</item>
///   <item>Otherwise it acts, then re-verifies; a step that still isn't satisfied fails typed.</item>
///   <item>A failure resumes on the next run: earlier satisfied steps are skipped, so a
///   <c>wsl --terminate</c> mid-bootstrap simply picks up where reality left off.</item>
/// </list>
/// Every failure is a <see cref="BootstrapException"/> carrying the failing step's name.
/// </summary>
public sealed class GitLoomOsBootstrapper
{
    private readonly IReadOnlyList<IBootstrapStep> _steps;

    public GitLoomOsBootstrapper(IReadOnlyList<IBootstrapStep> steps)
    {
        if (steps is null || steps.Count == 0)
            throw new ArgumentException("At least one bootstrap step is required.", nameof(steps));
        _steps = steps;
    }

    /// <summary>Builds the default ordered 6-step chain from a wired context.</summary>
    public static GitLoomOsBootstrapper Create(BootstrapContext ctx) => new(DefaultSteps(ctx));

    /// <summary>The canonical ordered step chain (contract §2, steps 1–6).</summary>
    public static IReadOnlyList<IBootstrapStep> DefaultSteps(BootstrapContext ctx) => new IBootstrapStep[]
    {
        new DetectDistroStep(ctx.Wsl),
        new ImportDistroStep(ctx.Wsl, ctx.FileSystem, ctx.Options),
        new WslConfigMergeStep(ctx.FileSystem),
        new FirstBootStep(ctx.Wsl),
        // The health probe doubles as the failure-explainer when it can (WslDaemonHealthProbe reads
        // the unit state + journal tail), so an unhealthy/crash-looping daemon names its actual
        // failure — for both the start step's re-check and the final health check.
        new StartDaemonStep(ctx.Wsl, ctx.HealthProbe as IDaemonHealthDiagnostics),
        new HealthCheckStep(ctx.HealthProbe, ctx.HealthProbe as IDaemonHealthDiagnostics),
    };

    /// <summary>The stage names in order, so the UI can render the checklist before the run starts.</summary>
    public IReadOnlyList<string> StepNames => _steps.Select(s => s.Name).ToList();

    /// <summary>
    /// Runs the chain to completion. Reports each stage's state transitions (and log tail) through
    /// <paramref name="progress"/>. Throws <see cref="BootstrapException"/> (naming the step) or
    /// <see cref="WslNotInstalledException"/> on failure; honours <paramref name="ct"/> between steps.
    /// </summary>
    public async Task RunAsync(IProgress<BootstrapProgress>? progress, CancellationToken ct)
    {
        foreach (var step in _steps)
        {
            ct.ThrowIfCancellationRequested();
            // The IsSatisfied re-check below can be slow (WSL/docker probes), especially on a relaunch
            // that resumes mid-step — surface a line so the UI isn't a blank spinner while it runs.
            progress?.Report(new BootstrapProgress(step.Name, BootstrapStageState.Running, "Checking current state…"));

            try
            {
                if (await step.IsSatisfiedAsync(ct).ConfigureAwait(false))
                {
                    // Empty (not null) log clears the transient "Checking current state…" line so an
                    // already-satisfied step shows clean as Done rather than keeping the checking text.
                    progress?.Report(new BootstrapProgress(step.Name, BootstrapStageState.Done, string.Empty));
                    continue;
                }

                var log = new Progress<string>(line =>
                    progress?.Report(new BootstrapProgress(step.Name, BootstrapStageState.Running, line)));
                await step.ExecuteAsync(log, ct).ConfigureAwait(false);

                // Re-verify: the act must have actually achieved the desired state. When it hasn't,
                // ask the step (if it can explain itself) WHY, so the error card names the unmet
                // condition instead of this dead-end generic line.
                if (!await step.IsSatisfiedAsync(ct).ConfigureAwait(false))
                {
                    string? why = null;
                    if (step is IBootstrapStepDiagnostics diagnostics)
                    {
                        try { why = await diagnostics.DescribeUnsatisfiedAsync(ct).ConfigureAwait(false); }
                        catch { /* diagnosis is best-effort; the failure below still surfaces */ }
                    }
                    throw new BootstrapException(step.Name, why is null
                        ? $"Step '{step.Name}' ran but its state check still failed."
                        : $"Step '{step.Name}' ran but its state check still failed: {why}");
                }

                // Empty (not null) log clears the step's last transient line (e.g. "Starting the
                // gitloomd service…") — a Done row that keeps its old in-progress text reads as stuck.
                progress?.Report(new BootstrapProgress(step.Name, BootstrapStageState.Done, string.Empty));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (BootstrapException)
            {
                progress?.Report(new BootstrapProgress(step.Name, BootstrapStageState.Failed, null));
                throw;
            }
            catch (Exception ex)
            {
                // Any other failure is wrapped so the failing stage is always named (typed).
                progress?.Report(new BootstrapProgress(step.Name, BootstrapStageState.Failed, null));
                throw new BootstrapException(step.Name, $"Step '{step.Name}' failed: {ex.Message}", ex);
            }
        }
    }
}
