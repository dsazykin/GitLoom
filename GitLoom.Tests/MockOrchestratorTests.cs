using System;
using System.Linq;
using System.Threading.Tasks;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Xunit;

namespace GitLoom.Tests;

// Lane E Part 3: the scripted daemon stand-in must honor the same invariants the real
// queue/kill-switch will (P2-10 stale cascade, gate reasons, freeze-first kill, plan
// approval spawning) — the prototype's ViewModels are only as honest as this mock.
public class MockOrchestratorTests
{
    // A long tick keeps the timer out of the way; tests drive transitions via the API.
    private static MockOrchestrator NewMock() => new(TimeSpan.FromHours(1));

    [Fact]
    public async Task ConfirmMerge_FliesTheStaleCascade()
    {
        using var mock = NewMock();

        // loom-3 is Verified+fresh but has 2 unacked flagged items — gate must hold.
        Assert.False(mock.CanMerge("loom-3", out var reason));
        Assert.Contains("flagged", reason);

        foreach (var item in mock.GetQueue().First(q => q.AgentId == "loom-3").FlaggedItems)
            await mock.AcknowledgeFlaggedChangeAsync("loom-3", item.Id);

        Assert.True(mock.CanMerge("loom-3", out _));
        var shaBefore = mock.MainSha;
        await mock.ConfirmMergeAsync("loom-3");

        Assert.NotEqual(shaBefore, mock.MainSha);
        var merged = mock.GetQueue().First(q => q.AgentId == "loom-3");
        Assert.Equal(WorkerMergeState.Merged, merged.State);
        // No other Verified/AwaitingReview entry may survive the cascade fresh (P2-10 step 3).
        Assert.DoesNotContain(mock.GetQueue(), q =>
            q.AgentId != "loom-3" && q.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview);
    }

    [Fact]
    public async Task ConfirmMerge_OnUnmergeableEntry_Throws()
    {
        using var mock = NewMock();
        await Assert.ThrowsAsync<InvalidOperationException>(() => mock.ConfirmMergeAsync("loom-4"));
    }

    [Fact]
    public async Task KillSwitch_FreezesQueueFirst_AndResumeRestores()
    {
        using var mock = NewMock();
        await mock.EngageAsync();

        // The freeze is instant and precedes the yield fan-out (OPS §4.5 / SA-1).
        Assert.True(mock.IsFrozen);
        Assert.False(mock.CanMerge("loom-3", out var reason));
        Assert.Contains("frozen", reason);

        await mock.ResumeAsync();
        Assert.False(mock.IsFrozen);
        Assert.Equal(KillSwitchPhase.Armed, mock.Phase);
    }

    [Fact]
    public async Task PlanApproval_SpawnsWorker_RejectionSpawnsNothing()
    {
        using var mock = NewMock();
        var plan = Assert.Single(mock.GetPendingPlans());

        await mock.SubmitPlanDecisionAsync(plan.PlanId, approve: true);

        Assert.Empty(mock.GetPendingPlans());
        var spawned = mock.ListAgents().FirstOrDefault(a => a.Name == "Loom-5");
        Assert.NotNull(spawned);
        Assert.Equal(AgentLifecycleState.Provisioning, spawned!.State);

        // Re-deciding a decided plan is a no-op, never a double spawn.
        await mock.SubmitPlanDecisionAsync(plan.PlanId, approve: true);
        Assert.Single(mock.ListAgents(), a => a.Name == "Loom-5");
    }

    [Fact]
    public async Task PromptQueue_QueuesAndCancels()
    {
        using var mock = NewMock();
        await mock.SendPromptAsync("loom-4", "also update the docs");
        await mock.SendPromptAsync("loom-4", "run the lint pass");
        Assert.Equal(2, mock.GetQueuedPrompts("loom-4").Count);

        await mock.CancelQueuedPromptAsync("loom-4", 0);
        Assert.Equal("run the lint pass", Assert.Single(mock.GetQueuedPrompts("loom-4")));
    }

    [Fact]
    public async Task Publish_WalksTheDeployPhases_ToALiveUrl()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromMilliseconds(25));
        await mock.PublishAsync();
        Assert.Equal(DeployPhase.Saving, mock.Deploy.Phase);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (mock.Deploy.Phase != DeployPhase.Live && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        Assert.Equal(DeployPhase.Live, mock.Deploy.Phase);
        Assert.False(string.IsNullOrEmpty(mock.Deploy.LiveUrl));
    }

    [Fact]
    public async Task PauseResumeEnd_DriveTheLifecycle()
    {
        using var mock = NewMock();

        await mock.PauseAgentAsync("loom-4");
        var paused = mock.ListAgents().First(a => a.AgentId == "loom-4");
        Assert.Equal(AgentLifecycleState.Paused, paused.State);
        Assert.Contains(mock.GetAgentUsage(), u => u.AgentId == "loom-4" && u.IsPaused);

        await mock.ResumeAgentAsync("loom-4");
        Assert.Equal(AgentLifecycleState.Working, mock.ListAgents().First(a => a.AgentId == "loom-4").State);

        await mock.EndAgentAsync("loom-4");
        Assert.Equal(AgentLifecycleState.Rejected, mock.ListAgents().First(a => a.AgentId == "loom-4").State);
        Assert.Equal(WorkerMergeState.Rejected, mock.GetQueue().First(q => q.AgentId == "loom-4").State);
        // Ending twice is a no-op, never a double teardown.
        await mock.EndAgentAsync("loom-4");
        Assert.Single(mock.ListAgents(), a => a.AgentId == "loom-4");
    }

    [Fact]
    public void AgentUsage_RowsExistForEveryLiveAgent()
    {
        using var mock = NewMock();
        var usage = mock.GetAgentUsage();
        Assert.Equal(mock.ListAgents().Count, usage.Count);
        Assert.All(usage, u => Assert.False(string.IsNullOrEmpty(u.Task)));
    }

    [Fact]
    public void TriageGate_Option2_HasAGreenCheckpointToOffer()
    {
        using var mock = NewMock();
        Assert.NotNull(mock.LastVerifiedGreen); // P3-02: option 2 enabled only when this is non-null
    }
}
