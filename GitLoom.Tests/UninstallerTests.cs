using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Bootstrap;

namespace GitLoom.Tests;

/// <summary>TI-P2-22 #8 (the automatable core of the manual matrix): the uninstall orchestration —
/// ordered, failure-tolerant, GitLoomEnv-scoped, and G-12 (personal distros untouched).</summary>
public class UninstallerTests
{
    private sealed class FakeWsl : IWslRunner
    {
        public readonly List<IReadOnlyList<string>> Calls = new();
        private bool _terminated;

        public Task<WslRunResult> RunAsync(IReadOnlyList<string> args, string? stdin, CancellationToken ct)
        {
            Calls.Add(args);
            if (args.SequenceEqual(WslCommands.Terminate()))
            {
                _terminated = true;
                return Task.FromResult(new WslRunResult(0, "", ""));
            }
            if (args.SequenceEqual(WslCommands.ListRunning()))
            {
                // A personal distro (Ubuntu) is running the whole time; GitLoomEnv stops after terminate.
                var running = _terminated ? "Ubuntu\n" : "Ubuntu\nGitLoomEnv\n";
                return Task.FromResult(new WslRunResult(0, running, ""));
            }
            return Task.FromResult(new WslRunResult(0, "", ""));
        }
    }

    private sealed class FakeRegistry : IRegistryCommandRunner
    {
        public readonly List<IReadOnlyList<string>> Calls = new();
        public Task<bool> RunAsync(IReadOnlyList<string> regArgs, CancellationToken ct)
        {
            Calls.Add(regArgs);
            return Task.FromResult(true);
        }
    }

    private static Uninstaller Build(FakeWsl wsl, FakeRegistry reg,
        Func<bool, CancellationToken, Task>? removeAppData = null) =>
        new(wsl, reg,
            stopDaemon: _ => Task.CompletedTask,
            removeScheduledTasks: _ => Task.CompletedTask,
            removeAppData: removeAppData ?? ((_, _) => Task.CompletedTask),
            terminatePollDelay: TimeSpan.FromMilliseconds(1));

    [Fact]
    public async Task Run_ShouldExecuteOrderedSteps_AndUnregisterOnlyGitLoomEnv()
    {
        var wsl = new FakeWsl();
        var reg = new FakeRegistry();
        var report = await Build(wsl, reg).RunAsync(new UninstallOptions());

        Assert.True(report.Clean);
        Assert.True(report.DistroUnregistered);
        Assert.Equal(
            new[] { "stop-daemon", "terminate-distro", "poll-stopped", "unregister-distro", "remove-registry", "remove-scheduled-tasks", "remove-appdata" },
            report.StepsRun);

        // Only GitLoomEnv was terminated/unregistered.
        Assert.Contains(wsl.Calls, c => c.SequenceEqual(WslCommands.Terminate()));
        Assert.Contains(wsl.Calls, c => c.SequenceEqual(WslCommands.Unregister()));
        Assert.All(wsl.Calls, c => Assert.All(c, a => Assert.NotEqual("--shutdown", a)));
    }

    [Fact]
    public async Task Run_ShouldLeavePersonalDistrosRunning_G12()
    {
        var wsl = new FakeWsl();
        var report = await Build(wsl, new FakeRegistry()).RunAsync(new UninstallOptions());

        Assert.Equal(new[] { "Ubuntu" }, report.PersonalDistrosBefore);
        Assert.Contains("Ubuntu", report.RunningDistrosAfter);      // personal distro still running
        Assert.DoesNotContain("GitLoomEnv", report.RunningDistrosAfter); // ours stopped
    }

    [Fact]
    public async Task Run_ShouldRemoveIntegrationRegistryKeys()
    {
        var reg = new FakeRegistry();
        await Build(new FakeWsl(), reg).RunAsync(new UninstallOptions());

        var deletedKeys = reg.Calls.Select(c => c[1]).ToArray();
        foreach (var owned in WindowsIntegration.OwnedKeyRoots())
            Assert.Contains(owned, deletedKeys);
    }

    [Fact]
    public async Task Run_ShouldBeFailureTolerant_ContinuingPastAThrowingStep()
    {
        var wsl = new FakeWsl();
        var reg = new FakeRegistry();
        Func<bool, CancellationToken, Task> boom = (_, _) => throw new InvalidOperationException("disk busy");

        var report = await Build(wsl, reg, removeAppData: boom).RunAsync(new UninstallOptions());

        Assert.False(report.Clean);
        Assert.Single(report.Errors);
        // The unregister still happened despite the later appdata failure.
        Assert.True(report.DistroUnregistered);
        Assert.Contains("remove-appdata", report.StepsRun);
    }

    [Fact]
    public async Task Run_WithRemoveSyncRemote_ShouldAppendThatStep()
    {
        var report = await Build(new FakeWsl(), new FakeRegistry())
            .RunAsync(new UninstallOptions(RemoveSyncRemote: true));

        Assert.Contains("remove-sync-remote", report.StepsRun);
    }
}
