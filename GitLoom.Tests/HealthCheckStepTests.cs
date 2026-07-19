using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Git.Exceptions;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// Regression tests for the daemon health check's crash-loop handling. A crash-looping gitloomd
/// (systemd restarts it every few seconds, so a process-existence probe flaps true/false) used to
/// slip one lucky "healthy" past <see cref="HealthCheckStep"/> and then die on the bootstrapper's
/// post-run re-check with the dead-end "Step 'Health-check daemon' ran but its state check still
/// failed" — telling the user nothing while the daemon was SIGABRT-looping. Now: health must be
/// STABLE (consecutive successes), and every failure path names the daemon's actual state through
/// <see cref="IDaemonHealthDiagnostics"/>.
/// </summary>
public class HealthCheckStepTests
{
    [Fact]
    public async Task CrashLoopFlap_IsNotHealthy_UntilStable()
    {
        // healthy → crashed → healthy → healthy: the single flap must not complete the step.
        var probe = new ScriptedProbe(true, false, true, true);
        var step = new HealthCheckStep(probe, diagnostics: null, attempts: 10, delay: TimeSpan.Zero);

        await step.ExecuteAsync(new CollectingLog(), CancellationToken.None);

        // 4 probes: the flap reset the consecutive counter; only the last two count as stable.
        Assert.Equal(4, probe.Probes);
    }

    [Fact]
    public async Task NeverHealthy_FailureNamesTheDaemonsActualState()
    {
        var probe = new ScriptedProbe(false);
        var diagnostics = new FakeDiagnostics(
            "The gitloomd service inside GitLoomEnv is 'activating'. Recent log: "
            + "at System.IO.Directory.CreateDirectory(String path) | Main process exited, code=killed, status=6/ABRT");
        var step = new HealthCheckStep(probe, diagnostics, attempts: 3, delay: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<BootstrapException>(
            () => step.ExecuteAsync(new CollectingLog(), CancellationToken.None));

        Assert.Contains("did not report healthy", ex.Message);
        Assert.Contains("'activating'", ex.Message);
        Assert.Contains("status=6/ABRT", ex.Message);
    }

    [Fact]
    public async Task NeverHealthy_WithoutDiagnostics_KeepsTheGenericMessage()
    {
        var step = new HealthCheckStep(new ScriptedProbe(false), diagnostics: null, attempts: 2, delay: TimeSpan.Zero);

        var ex = await Assert.ThrowsAsync<BootstrapException>(
            () => step.ExecuteAsync(new CollectingLog(), CancellationToken.None));

        Assert.Equal("The Mainguard daemon did not report healthy in time.", ex.Message);
    }

    [Fact]
    public async Task Bootstrapper_RecheckFailure_IncludesTheStepsDiagnosis()
    {
        // initial check false → execute sees two stable healthy answers → post-run re-check flaps
        // false again (the crash-loop window). The re-check failure must carry the diagnosis.
        var probe = new ScriptedProbe(false, true, true, false);
        var diagnostics = new FakeDiagnostics("The gitloomd service inside GitLoomEnv is 'activating'.");
        var step = new HealthCheckStep(probe, diagnostics, attempts: 5, delay: TimeSpan.Zero);
        var bootstrapper = new GitLoomOsBootstrapper(new IBootstrapStep[] { step });

        var ex = await Assert.ThrowsAsync<BootstrapException>(
            () => bootstrapper.RunAsync(progress: null, CancellationToken.None));

        Assert.Contains("state check still failed", ex.Message);
        Assert.Contains("'activating'", ex.Message);
    }

    [Fact]
    public async Task Bootstrapper_ExecutedDone_ClearsTheLingeringLogLine()
    {
        // A step that executes (initial check false, then healthy twice, re-check healthy): its Done
        // report must carry an EMPTY log so the UI clears the last "Waiting…" line instead of showing
        // it forever next to a Done checkmark (reads as stuck).
        var probe = new ScriptedProbe(false, true, true, true);
        var step = new HealthCheckStep(probe, diagnostics: null, attempts: 5, delay: TimeSpan.Zero);
        var bootstrapper = new GitLoomOsBootstrapper(new IBootstrapStep[] { step });
        var progress = new CollectingProgress();

        await bootstrapper.RunAsync(progress, CancellationToken.None);

        var done = progress.Snapshot().Last(r => r.State == BootstrapStageState.Done);
        Assert.Equal(string.Empty, done.Log);
    }

    // ---- fakes -----------------------------------------------------------------------------------

    /// <summary>Plays back a scripted health sequence; the final answer repeats forever.</summary>
    private sealed class ScriptedProbe : IDaemonHealthProbe
    {
        private readonly bool[] _script;
        public int Probes { get; private set; }

        public ScriptedProbe(params bool[] script) => _script = script;

        public Task<bool> IsHealthyAsync(CancellationToken ct) =>
            Task.FromResult(_script[Math.Min(Probes++, _script.Length - 1)]);
    }

    private sealed class FakeDiagnostics : IDaemonHealthDiagnostics
    {
        private readonly string? _description;
        public FakeDiagnostics(string? description) => _description = description;
        public Task<string?> DescribeUnhealthyAsync(CancellationToken ct) => Task.FromResult(_description);
    }

    private sealed class CollectingLog : IProgress<string>
    {
        public List<string> Lines { get; } = new();
        public void Report(string value) => Lines.Add(value);
    }

    /// <summary>Thread-safe sink: the bootstrapper's inner step-log lines arrive via
    /// <see cref="Progress{T}"/> (async posts), so reports can land concurrently with assertions.</summary>
    private sealed class CollectingProgress : IProgress<BootstrapProgress>
    {
        private readonly List<BootstrapProgress> _sink = new();
        public void Report(BootstrapProgress value) { lock (_sink) _sink.Add(value); }
        public BootstrapProgress[] Snapshot() { lock (_sink) return _sink.ToArray(); }
    }
}
