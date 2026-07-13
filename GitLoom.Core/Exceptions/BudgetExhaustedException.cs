namespace GitLoom.Core.Exceptions;

/// <summary>
/// Thrown when an agent has hit a per-agent or per-day token/cost cap (P2-08). The gateway catches
/// this and <b>pauses</b> the agent with this typed reason — it never kills the container (killing on
/// budget exhaustion is a rejection trigger). The <see cref="AgentId"/> and <see cref="Reason"/> flow
/// to the UI and the <c>budget_exceeded</c> audit event.
/// </summary>
public sealed class BudgetExhaustedException : GitLoomException
{
    public BudgetExhaustedException(string agentId, string reason)
        : base(reason)
    {
        AgentId = agentId;
        Reason = reason;
    }

    /// <summary>The agent whose budget is exhausted.</summary>
    public string AgentId { get; }

    /// <summary>The honest, user-facing reason (which cap, and its value).</summary>
    public string Reason { get; }
}
