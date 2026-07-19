using Mainguard.Agents.Agents;

namespace GitLoom.App.ViewModels.Agents;

/// <summary>
/// Pure derivation of the section-rail attention flag (P2-13 §3): an agent needs the human's
/// attention when it is awaiting review, blocked on a conflict, or waiting on human input, or
/// when the coordinator has a plan pending approval (P2-14 feeds the plan flag later). No side
/// effects, no UI — unit-pinned by <c>AttentionDerivationTests</c>.
/// </summary>
public static class AttentionPolicy
{
    /// <summary>True when this badge status is one the human must act on.</summary>
    public static bool IsAttentionRequired(AgentStatus status) =>
        status is AgentStatus.AwaitingReview or AgentStatus.Conflict;

    /// <summary>
    /// The rail-wide flag: any agent needing attention, or a coordinator plan pending approval.
    /// </summary>
    public static bool IsAttentionRequired(AgentStatus status, bool planApprovalPending) =>
        planApprovalPending || IsAttentionRequired(status);

    /// <summary>Convenience over the OPS lifecycle enum (maps through <see cref="AgentStatusMap"/>).</summary>
    public static bool IsAttentionRequired(AgentLifecycleState state) =>
        IsAttentionRequired(AgentStatusMap.FromLifecycle(state));

    /// <summary>
    /// Whether a transition into <paramref name="to"/> is a "waiting/blocked" edge worth an OS
    /// notification (P2-13 §6): entering AwaitingReview or Conflict, but not a lateral move that
    /// was already in that set.
    /// </summary>
    public static bool IsWaitingOrBlockedTransition(AgentStatus from, AgentStatus to) =>
        IsAttentionRequired(to) && !IsAttentionRequired(from);
}
