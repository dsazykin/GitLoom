namespace Mainguard.Agents.Agents;

/// <summary>
/// The orchestration roles a spawned agent session can carry (the free-form-string contract of
/// <c>SpawnAgentRequest.role</c> / <c>AgentInfo.role</c>). Shared by the daemon (spawn workflow,
/// terminal locking) and the App (coordinator surface, subagent badging).
/// </summary>
public static class AgentRoles
{
    /// <summary>A manually started agent (the default; empty string on the wire).</summary>
    public const string Manual = "";

    /// <summary>The operator-facing coordinator CLI: its jail gets the daemon-mediated
    /// <c>gitloom-agent</c> spawn channel, and its terminal is fully interactive.</summary>
    public const string Coordinator = "coordinator";

    /// <summary>A coordinator-spawned worker: appears as a subagent in the activity bar, and its
    /// terminal input is daemon-locked (P2-14 — read-only, steering goes through prompts).</summary>
    public const string Managed = "managed";
}
