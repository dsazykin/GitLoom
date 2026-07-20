using System;

namespace Mainguard.Git.Models;

/// <summary>
/// The daemon's expected-agent record (P2-08 swarm reconciler). On boot the reconciler compares this
/// table against the live Docker containers labeled <c>mainguard.agent</c> — <b>Docker is the sole
/// source of truth for liveness</b> (no PID/lock files). A row present here whose container is gone
/// is pruned and marked <see cref="Disposition"/> = <c>Dead</c> with a <see cref="DisposalReason"/>.
/// </summary>
public class ExpectedAgent
{
    /// <summary>Auto-increment primary key.</summary>
    public long Id { get; set; }

    /// <summary>The repository hash the agent's worktree/jail belongs to.</summary>
    public string RepoHash { get; set; } = string.Empty;

    /// <summary>The agent id (matches the <c>mainguard.agent</c> container label).</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Last known disposition: <c>Live</c>, <c>Dead</c>, or <c>Adopted</c>.</summary>
    public string Disposition { get; set; } = "Live";

    /// <summary>Why the agent was disposed (only set when <see cref="Disposition"/> is <c>Dead</c>).</summary>
    public string? DisposalReason { get; set; }
}
