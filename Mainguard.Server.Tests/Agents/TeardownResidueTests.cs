using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Orchestrator;
using Mainguard.Server.Tests.Fixtures;
using Xunit;

namespace Mainguard.Server.Tests.Agents;

/// <summary>
/// TI-P2-09 test 8: agent teardown leaves no residue. On a real provisioned mirror + worktree (no
/// Docker), disposing the <see cref="AgentContext"/> removes the worktree with force, deletes
/// <c>agent/&lt;id&gt;</c>, emits the terminal event the client reacts to, and the verify pass reports a
/// clean <c>git worktree list</c> — the report fails the test on any residue.
/// </summary>
public sealed class TeardownResidueTests
{
    [Fact]
    public async Task Teardown_NoResidue_EmitsTerminalEvent()
    {
        using var fixture = new DualRepoFixture();
        var vmRoot = AgentTestGit.NewVmRoot();
        try
        {
            var provisioner = new RepoProvisioner(vmRoot);
            var hash = provisioner.Provision(fixture.WorkRepoPath).RepoHash;
            var worktrees = new WorktreeManager(vmRoot);
            var worktreePath = worktrees.CreateAgentWorktree(hash, "a1");
            Assert.True(Directory.Exists(worktreePath));

            var events = new List<AgentLifecycleEvent>();
            var ptyKilled = false;
            var containerStopped = false;

            var plan = new TeardownPlan(
                AgentId: "a1",
                KillPty: _ => { ptyKilled = true; return Task.CompletedTask; },
                StopContainer: _ => { containerStopped = true; return Task.CompletedTask; },
                RemoveWorktree: () => worktrees.RemoveAgentWorktree(hash, "a1", force: true),
                ResidualWorktrees: () => worktrees.List(hash)
                    .Where(w => w.Branch == "agent/a1")
                    .Select(w => w.Path)
                    .ToList(),
                ResidualContainers: () => Array.Empty<string>(), // no Docker in this leg
                Emit: events.Add);

            var context = new AgentContext(plan);
            var report = await context.TeardownAsync();

            // Ordered steps ran.
            Assert.True(ptyKilled);
            Assert.True(containerStopped);

            // No residue on disk or in the mirror.
            Assert.False(Directory.Exists(worktreePath));
            Assert.DoesNotContain(worktrees.List(hash), w => w.Branch == "agent/a1");
            Assert.NotEqual(0, AgentTestGit.Run(Path.Combine(vmRoot, "repos", hash + ".git"),
                "rev-parse", "--verify", "--quiet", "refs/heads/agent/a1").Code);

            // The report is clean and the terminal event fired; no residue event.
            Assert.True(report.Clean);
            Assert.Empty(report.ResidualWorktrees);
            Assert.True(report.EmittedTerminal);
            Assert.Contains(events, e => e.Kind == AgentLifecycleEvent.Terminated);
            Assert.DoesNotContain(events, e => e.Kind == AgentLifecycleEvent.Residue);
        }
        finally
        {
            AgentTestGit.DeleteTree(vmRoot);
        }
    }

    [Fact]
    public async Task Teardown_IsIdempotent_AndFailureTolerant()
    {
        var events = new List<AgentLifecycleEvent>();
        var removeCalls = 0;

        var plan = new TeardownPlan(
            AgentId: "a1",
            KillPty: _ => throw new InvalidOperationException("PTY already gone"), // a failing step must not stop teardown
            RemoveWorktree: () => removeCalls++,
            Emit: events.Add);

        var context = new AgentContext(plan);
        var first = await context.TeardownAsync();

        // The failing KillPty was aggregated, later steps still ran, the terminal event still fired.
        Assert.Single(first.Errors);
        Assert.Equal(1, removeCalls);
        Assert.True(first.EmittedTerminal);

        // Second dispose is a no-op (idempotent) — RemoveWorktree not called again.
        await context.TeardownAsync();
        Assert.Equal(1, removeCalls);
    }
}
