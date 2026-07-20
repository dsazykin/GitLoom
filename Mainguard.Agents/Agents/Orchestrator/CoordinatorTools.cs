using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Git.Audit;

// In the Orchestrator namespace, the parent Mainguard.Agents.Agents (AdmissionController, …) is in scope
// automatically, and `TaskPlan` resolves to the domain type here (shadowing the UI prototype), which is
// exactly what the two-phase gate needs.
namespace Mainguard.Agents.Agents.Orchestrator;

/// <summary>The status of a coordinator tool call.</summary>
public enum CoordinatorToolStatus
{
    /// <summary>The tool succeeded (e.g. a plan was drafted for approval).</summary>
    Ok,

    /// <summary>The tool was refused by a hard cap (worker cap / admission / budget) or a frozen queue.</summary>
    Rejected,

    /// <summary>The S-8 drafting cap was hit (maps to gRPC <c>RESOURCE_EXHAUSTED</c>).</summary>
    ResourceExhausted,
}

/// <summary>A tool call's typed result, surfaced back into the coordinator's chat loop.</summary>
public sealed record CoordinatorToolResult(CoordinatorToolStatus Status, string Message, string? PlanId = null)
{
    public bool IsOk => Status == CoordinatorToolStatus.Ok;

    public static CoordinatorToolResult Ok(string message, string? planId = null) => new(CoordinatorToolStatus.Ok, message, planId);
    public static CoordinatorToolResult Rejected(string message) => new(CoordinatorToolStatus.Rejected, message);
    public static CoordinatorToolResult Exhausted(string message) => new(CoordinatorToolStatus.ResourceExhausted, message);
}

/// <summary>The daemon-side worker seam the coordinator's read/steer/verify tools reach through.</summary>
public interface IWorkerControl
{
    /// <summary>The live worker ids (running agents this coordinator can see).</summary>
    IReadOnlyList<string> ActiveWorkerIds { get; }

    /// <summary>A one-line status word for a worker (or null when unknown).</summary>
    string? WorkerStatus(string agentId);

    /// <summary>Sends a steering prompt to a managed worker (capped — only to workers the coordinator owns).</summary>
    Task SendPromptAsync(string agentId, string prompt, CancellationToken ct);

    /// <summary>Requests verification of a worker's branch (the P2-10 verify path).</summary>
    Task RequestVerificationAsync(string agentId, CancellationToken ct);
}

/// <summary>Hard caps applied to coordinator tool calls (limits/budgets/admission — plan §2).</summary>
public sealed record CoordinatorLimits(int MaxActiveWorkers = 6);

/// <summary>
/// P2-14 coordinator tool surface (contract §2). The four tools — <c>spawn_worker</c>,
/// <c>get_worker_status</c>, <c>send_worker_prompt</c>, <c>request_verification</c> — each capped by
/// limits/budgets/admission. <c>spawn_worker</c> is the two-phase gate: it never spawns directly — it
/// drafts a <see cref="TaskPlan"/> as a pending plan (S-8-capped in <see cref="PlanApprovalService"/>), and
/// a worker starts only when a human approves. The coordinator has no worktree, no git credentials, no
/// code, and no merge power — this surface is all it can do.
/// </summary>
public sealed class CoordinatorTools
{
    private readonly string _coordinatorId;
    private readonly PlanApprovalService _plans;
    private readonly AdmissionController _admission;
    private readonly Func<bool> _budgetExceeded;
    private readonly Func<int> _activeWorkerCount;
    private readonly IWorkerControl _workers;
    private readonly KillSwitchGate _killGate;
    private readonly CoordinatorLimits _limits;

    public CoordinatorTools(
        string coordinatorId,
        PlanApprovalService plans,
        AdmissionController admission,
        IWorkerControl workers,
        Func<int>? activeWorkerCount = null,
        Func<bool>? budgetExceeded = null,
        KillSwitchGate? killGate = null,
        CoordinatorLimits? limits = null)
    {
        _coordinatorId = string.IsNullOrWhiteSpace(coordinatorId)
            ? throw new ArgumentException("coordinatorId is required.", nameof(coordinatorId))
            : coordinatorId;
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _workers = workers ?? throw new ArgumentNullException(nameof(workers));
        _activeWorkerCount = activeWorkerCount ?? (() => _workers.ActiveWorkerIds.Count);
        _budgetExceeded = budgetExceeded ?? (() => false);
        _killGate = killGate ?? new KillSwitchGate();
        _limits = limits ?? new CoordinatorLimits();
    }

    /// <summary>
    /// <c>spawn_worker(taskSpec)</c> — the two-phase gate. Applies the hard caps (frozen queue → refuse;
    /// active-worker cap; admission; per-day budget), then drafts a pending plan (S-8-capped). It NEVER
    /// spawns a worker directly — a human must approve the drafted plan first (SA-1/F2 path).
    /// </summary>
    public CoordinatorToolResult SpawnWorker(string title, TaskPlanFields fields, string taskPrompt, decimal budgetUsd)
    {
        ArgumentNullException.ThrowIfNull(fields);

        // Frozen (kill switch) → refuse loudly (SA-1/F4 — spawn is one of the frozen paths).
        if (_killGate.IsFrozen)
        {
            return CoordinatorToolResult.Rejected("Everything is frozen (kill switch engaged) — resume before spawning.");
        }

        // Active-worker cap (admission is about RUNNING agents; pending plans consume none — S-8).
        var active = _activeWorkerCount();
        if (active >= _limits.MaxActiveWorkers)
        {
            return CoordinatorToolResult.Rejected(
                $"Worker cap reached — {active}/{_limits.MaxActiveWorkers} running. Let one finish first.");
        }

        // Admission (VM memory headroom).
        if (!_admission.CanSpawn(out var admissionReason))
        {
            return CoordinatorToolResult.Rejected(admissionReason);
        }

        // Per-day budget.
        if (_budgetExceeded())
        {
            return CoordinatorToolResult.Rejected("The per-day budget is exhausted — no new workers today.");
        }

        // Two-phase: draft a pending plan (S-8 pending-cap + rate limit live here).
        var draft = _plans.Draft(_coordinatorId, title, fields, taskPrompt, budgetUsd);
        if (draft.Outcome == DraftOutcome.ResourceExhausted)
        {
            return CoordinatorToolResult.Exhausted(draft.Message);
        }

        return CoordinatorToolResult.Ok(draft.Message, draft.PlanId);
    }

    /// <summary><c>get_worker_status</c> — read-only. All workers, or one when <paramref name="agentId"/> is given.</summary>
    public CoordinatorToolResult GetWorkerStatus(string? agentId = null)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
        {
            var status = _workers.WorkerStatus(agentId);
            return status is null
                ? CoordinatorToolResult.Rejected($"No worker '{agentId}'.")
                : CoordinatorToolResult.Ok($"{agentId}: {status}");
        }

        var ids = _workers.ActiveWorkerIds;
        if (ids.Count == 0)
        {
            return CoordinatorToolResult.Ok("No workers running.");
        }

        var lines = ids.Select(id => $"{id}: {_workers.WorkerStatus(id) ?? "Unknown"}");
        return CoordinatorToolResult.Ok(string.Join("; ", lines));
    }

    /// <summary><c>send_worker_prompt</c> — steer a managed worker (only workers the coordinator owns).</summary>
    public async Task<CoordinatorToolResult> SendWorkerPromptAsync(string agentId, string prompt, CancellationToken ct = default)
    {
        if (_killGate.IsFrozen)
        {
            return CoordinatorToolResult.Rejected("Everything is frozen (kill switch engaged) — resume first.");
        }

        if (_workers.WorkerStatus(agentId) is null)
        {
            return CoordinatorToolResult.Rejected($"No worker '{agentId}'.");
        }

        await _workers.SendPromptAsync(agentId, prompt, ct).ConfigureAwait(false);
        return CoordinatorToolResult.Ok($"Prompt sent to {agentId}.");
    }

    /// <summary><c>request_verification</c> — kick off the P2-10 verify for a worker (never a merge).</summary>
    public async Task<CoordinatorToolResult> RequestVerificationAsync(string agentId, CancellationToken ct = default)
    {
        if (_workers.WorkerStatus(agentId) is null)
        {
            return CoordinatorToolResult.Rejected($"No worker '{agentId}'.");
        }

        await _workers.RequestVerificationAsync(agentId, ct).ConfigureAwait(false);
        return CoordinatorToolResult.Ok($"Verification requested for {agentId}.");
    }
}
