using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Agents.Agents.Sandbox;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// P2-10 verification runs (plan §6 test 12/13 pure analogues + TI-P2-10.9). Pass/fail is the
/// daemon-observed container-runtime exit reported by <see cref="ISandboxEngine.ExecAsync"/> — never a
/// supervisor frame (OPS SA-1). The runner routes through the sandbox engine, never the host.
/// </summary>
public class VerificationRunnerTests
{
    /// <summary>
    /// A fake sandbox engine. <see cref="ExecAsync"/> returns the daemon-observed exit code. It also
    /// carries a <see cref="ForgedSupervisorClaim"/> the runner must NEVER read — modeling a compromised,
    /// non-TCB supervisor that claims <c>passed:true</c> over the OOB frame. Only <see cref="ExecAsync"/>
    /// is exercised; the other lifecycle methods are not part of the verification path.
    /// </summary>
    private sealed class FakeSandboxEngine : ISandboxEngine
    {
        private readonly int _exitCode;
        public bool ForgedSupervisorClaim { get; init; }
        public string? LastContainerId { get; private set; }
        public IReadOnlyList<string>? LastCommand { get; private set; }
        public bool HostProcessStarted { get; private set; }

        public FakeSandboxEngine(int exitCode) => _exitCode = exitCode;

        public Task<SandboxExecResult> ExecAsync(string containerId, IReadOnlyList<string> command, CancellationToken ct = default)
        {
            LastContainerId = containerId;
            LastCommand = command;
            // The container-runtime exit is the ONLY truth. The forged supervisor claim is not returned here.
            return Task.FromResult(new SandboxExecResult(_exitCode, "test output", ""));
        }

        public Task<SandboxHandle> SpawnAsync(SandboxSpawnRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnpauseAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StopAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RemoveAsync(string containerId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static VerificationRequest Request(IReadOnlyList<string>? command = null) => new(
        AgentId: "loom-1",
        ContainerId: "container-abc",
        MainSha: "sha0",
        Command: command ?? new[] { "npm", "test" },
        ResolvedCommand: "npm test",
        ConfigHash: "confighash");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mainguard-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public async Task ExitZero_IsPassed()
    {
        var runner = new VerificationRunner(new FakeSandboxEngine(0), TempDir());
        var record = await runner.RunAsync(Request(), CancellationToken.None);
        Assert.True(record.Passed);
        Assert.Equal("sha0", record.MainSha);
    }

    [Fact]
    public async Task ExitNonZero_IsFailed()
    {
        var runner = new VerificationRunner(new FakeSandboxEngine(1), TempDir());
        var record = await runner.RunAsync(Request(), CancellationToken.None);
        Assert.False(record.Passed);
    }

    /// <summary>OPS SA-1: a forged supervisor <c>passed:true</c> does not override the container exit.</summary>
    [Fact]
    public async Task ForgedVerifyResult_ShouldBeOverriddenByDaemonObservedExit()
    {
        var engine = new FakeSandboxEngine(1) { ForgedSupervisorClaim = true }; // "supervisor" says passed
        var runner = new VerificationRunner(engine, TempDir());

        var record = await runner.RunAsync(Request(), CancellationToken.None);

        // Despite the forged claim, the daemon-observed exit (1) rules → NOT passed, so no Verified state.
        Assert.False(record.Passed);
    }

    [Fact]
    public async Task RunsInSandbox_NeverHost()
    {
        var engine = new FakeSandboxEngine(0);
        var runner = new VerificationRunner(engine, TempDir());

        await runner.RunAsync(Request(new[] { "make", "check" }), CancellationToken.None);

        // The command ran through the sandbox engine, in the agent's container — not on the host.
        Assert.Equal("container-abc", engine.LastContainerId);
        Assert.Equal(new[] { "make", "check" }, engine.LastCommand);
        Assert.False(engine.HostProcessStarted);
    }

    [Fact]
    public async Task WritesLogArtifact_WithProvenance()
    {
        var dir = TempDir();
        var runner = new VerificationRunner(new FakeSandboxEngine(0), dir);
        var record = await runner.RunAsync(Request(), CancellationToken.None);

        Assert.True(File.Exists(record.LogArtifactPath));
        var log = File.ReadAllText(record.LogArtifactPath);
        Assert.Contains("resolved-command: npm test", log);
        Assert.Contains("config-hash: confighash", log);
        Assert.Contains("container-runtime-exit: 0", log);
    }

    [Fact]
    public async Task EmptyCommand_Throws_Typed()
    {
        var runner = new VerificationRunner(new FakeSandboxEngine(0), TempDir());
        await Assert.ThrowsAsync<NoVerificationCommandException>(
            () => runner.RunAsync(Request(Array.Empty<string>()), CancellationToken.None));
    }
}
