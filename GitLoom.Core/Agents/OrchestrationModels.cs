using System;
using System.Collections.Generic;

namespace GitLoom.Core.Agents;

// Prototype data model for the Phase-2 control center (Lane E Part 3).
// Shapes mirror the future gRPC contract (OPS §3.4 events, §4.1/§4.2 state machines,
// P2-10 IMergeQueue, P2-14 TaskPlan) so the mock services can later be swapped for
// DaemonClient without touching ViewModels or Views. No daemon, no Docker, no agents
// exist behind these types today — see docs/design/ControlCenterDesign.md.

/// <summary>Agent session/process lifecycle — OPS §4.1 verbatim.</summary>
public enum AgentLifecycleState
{
    Requested, PlanPending, Provisioning, Working, Yielding, Paused,
    RateLimited, Unresponsive, AwaitingReview, ReviewHibernated,
    Merged, Rejected, Dead, TornDown,
}

/// <summary>Branch merge-eligibility lifecycle — the P2-10 enum verbatim (OPS §4.2).</summary>
public enum WorkerMergeState { Working, Verifying, Verified, StaleVerified, AwaitingReview, Merged, Rejected }

/// <summary>
/// Where a merge-queue entry came from (P2-12). <see cref="Local"/> is a locally-spawned agent whose
/// merge lands via the Windows foreground merge; <see cref="External"/> is an intake'd bot PR whose
/// merge is pushed back through the host PR merge API. The queue persists this per (repo, agent) so the
/// pluggable merge step (<c>MergeDispatch</c>) routes correctly after a daemon restart.
/// </summary>
public enum MergeEntryOrigin { Local, External }

public sealed record AgentInfo(
    string AgentId,
    string Name,             // N-4 working name, e.g. "Loom-3"
    string Branch,
    AgentLifecycleState State,
    string Detail,           // the one live fact for the list's detail slot (E4)
    DateTimeOffset SpawnedAt);

/// <summary>P2-10: immutable verification record tied to a main SHA.</summary>
public sealed record VerificationRecord(string AgentId, string MainSha, bool Passed, int TestsPassed, int TestsTotal, DateTimeOffset When);

/// <summary>P2-11: one must-acknowledge flagged item; acks bind to the diff hash daemon-side.</summary>
public sealed record FlaggedItem(string Id, string Path, string Category, string Fact, bool Acknowledged);

public sealed record QueueEntry(
    string AgentId,
    string Name,
    string Branch,
    WorkerMergeState State,
    string Detail,
    VerificationRecord? Verification,
    IReadOnlyList<FlaggedItem> FlaggedItems);

/// <summary>P2-14: the schema-validated plan a managed worker spawns from. Scope is load-bearing.</summary>
public sealed record TaskPlan(
    string PlanId,
    string Title,
    IReadOnlyList<string> Scope,
    string Approach,
    string TestStrategy,
    decimal BudgetUsd,
    DateTimeOffset DraftedAt);

public enum ChatLineKind
{
    Human,        // the operator's message
    Coordinator,  // the coordinator's reply (model output)
    ToolCall,     // one-line mono fact, collapsed group in the view
    SystemLine,   // OPS event rendered as a history line
    PlanCard,     // carries a PlanId — the view renders the TaskPlan approval card
}

public sealed record ChatLine(ChatLineKind Kind, string Text, DateTimeOffset At, string? PlanId = null);

/// <summary>OPS §3.4-shaped notification event: seq-ordered, dedup by seq, UI projection only.</summary>
public sealed record AgentEvent(long Seq, string Type, string AgentId, string Payload, DateTimeOffset At);

/// <summary>P2-44: one sandbox telemetry fact (egress denial, secret access attempt, …).</summary>
public sealed record SandboxEvent(DateTimeOffset At, string AgentId, string Kind, string Detail, string Process);

/// <summary>P2-13 activity-bar resource sample (VM CPU/RAM + gateway token spend).</summary>
public sealed record ResourceSample(DateTimeOffset At, double CpuPercent, double RamGb, decimal SpendTodayUsd);

/// <summary>One agent's live resource row for the task-manager-style monitor (revised 2026-07-11):
/// per-agent CPU/RAM/spend plus the state word and current task, so totals decompose.</summary>
public sealed record AgentResourceUsage(
    string AgentId, string Name, string StateWord, bool IsPaused,
    double CpuPercent, double RamGb, decimal SpendUsd, string Task);

/// <summary>OPS §4.5 kill-switch phases, rendered as the banner's fact line.</summary>
public enum KillSwitchPhase { Armed, QueueFrozen, PerAgentYield, Frozen, Snapshotted, Complete }

/// <summary>P3-01: a Vibe auto-checkpoint; VerifiedGreen gates triage option 2.</summary>
public sealed record Checkpoint(string Sha, string Summary, DateTimeOffset When, bool VerifiedGreen);

public enum DeployPhase { Idle, Saving, Uploading, Building, GoingLive, Live, Failed }

public sealed record DeployStatus(DeployPhase Phase, string? LiveUrl, string? FailureSummary);
