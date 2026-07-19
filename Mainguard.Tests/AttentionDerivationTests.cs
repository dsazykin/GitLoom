using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.ViewModels.Agents;
using Xunit;

namespace Mainguard.Tests;

// P2-13 test 3 (§5) / TI-P2-13.3: the attention flag is a pure derivation — awaiting review,
// a conflict, or a pending plan approval means the human is needed; everything else is not.
public class AttentionDerivationTests
{
    [Theory]
    // status,               planPending, expected
    [InlineData(AgentStatus.AwaitingReview, false, true)]
    [InlineData(AgentStatus.Conflict, false, true)]
    [InlineData(AgentStatus.Working, false, false)]
    [InlineData(AgentStatus.Verifying, false, false)]
    [InlineData(AgentStatus.Verified, false, false)]
    [InlineData(AgentStatus.Stale, false, false)]
    [InlineData(AgentStatus.RateLimited, false, false)]
    [InlineData(AgentStatus.Dead, false, false)]
    [InlineData(AgentStatus.Paused, false, false)]
    // a pending plan approval flips attention on regardless of the agent's own status
    [InlineData(AgentStatus.Working, true, true)]
    [InlineData(AgentStatus.Verified, true, true)]
    public void Attention_Derivation(AgentStatus status, bool planPending, bool expected)
    {
        Assert.Equal(expected, AttentionPolicy.IsAttentionRequired(status, planPending));
    }

    [Theory]
    [InlineData(AgentLifecycleState.AwaitingReview, true)]
    [InlineData(AgentLifecycleState.Unresponsive, true)]   // maps to Conflict → attention
    [InlineData(AgentLifecycleState.PlanPending, true)]     // maps to AwaitingReview → attention
    [InlineData(AgentLifecycleState.Working, false)]
    [InlineData(AgentLifecycleState.RateLimited, false)]
    [InlineData(AgentLifecycleState.Merged, false)]
    [InlineData(AgentLifecycleState.Paused, false)]
    public void Attention_Derivation_FromLifecycle(AgentLifecycleState state, bool expected)
    {
        Assert.Equal(expected, AttentionPolicy.IsAttentionRequired(state));
    }

    [Theory]
    [InlineData(AgentStatus.Working, AgentStatus.AwaitingReview, true)]   // entering the waiting set
    [InlineData(AgentStatus.Working, AgentStatus.Conflict, true)]
    [InlineData(AgentStatus.AwaitingReview, AgentStatus.Conflict, false)] // lateral within the set
    [InlineData(AgentStatus.AwaitingReview, AgentStatus.Working, false)]  // leaving the set
    [InlineData(AgentStatus.Verifying, AgentStatus.Verified, false)]
    public void WaitingOrBlockedTransition_OnlyFiresOnEntry(AgentStatus from, AgentStatus to, bool expected)
    {
        Assert.Equal(expected, AttentionPolicy.IsWaitingOrBlockedTransition(from, to));
    }
}
