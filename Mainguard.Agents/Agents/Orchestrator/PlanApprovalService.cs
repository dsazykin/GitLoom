using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Mainguard.Git.Audit;

namespace Mainguard.Agents.Agents.Orchestrator;

// In the Orchestrator namespace `TaskPlan` resolves to the canonical Mainguard.Agents.Agents.TaskPlan — the
// single plan type the P2-11 detector, the approval card, and the daemon all share.

/// <summary>The lifecycle of a two-phase-spawn plan (OPS §4.1 <c>PlanPending</c> maps to <see cref="Pending"/>).</summary>
public enum PlanStatus { Pending, Approved, Rejected }

/// <summary>Why a <see cref="PlanApprovalService.Draft"/> call was refused (S-8 anti-approval-fatigue).</summary>
public enum DraftOutcome { Drafted, ResourceExhausted }

/// <summary>
/// A plan the coordinator drafted and (optionally) a human decided. <see cref="ApproverIdentity"/> is
/// <b>daemon-derived</b> from the authenticated connection's OS peer credential and set only on approval —
/// there is no client-supplied identity anywhere in this record's write path (SA-1/F2).
/// </summary>
public sealed record PendingPlan(
    TaskPlan Plan,
    string CoordinatorId,
    string TaskPrompt,
    PlanStatus Status,
    string? ApproverIdentity,
    DateTimeOffset? DecidedAt)
{
    public string PlanId => Plan.PlanId;
    public string Title => Plan.Title;
    public decimal BudgetUsd => Plan.BudgetUsd;
    public DateTimeOffset DraftedAt => Plan.DraftedAt;
}

/// <summary>The result of a <see cref="PlanApprovalService.Draft"/> call.</summary>
public sealed record DraftResult(DraftOutcome Outcome, string Message, string? PlanId)
{
    public bool IsDrafted => Outcome == DraftOutcome.Drafted;
}

/// <summary>Anti-approval-fatigue + drafting limits (S-8). Sane defaults; overridable in config.</summary>
public sealed record PlanApprovalOptions(
    int MaxPendingPerCoordinator = 5,
    int MaxDraftsPerWindow = 10,
    TimeSpan? DraftWindow = null)
{
    public TimeSpan DraftWindowOrDefault => DraftWindow ?? TimeSpan.FromMinutes(1);
}

/// <summary>The persistence seam for plans (daemon-side, restart-safe). In-memory in most tests.</summary>
public interface IPlanApprovalStore
{
    /// <summary>All persisted plans (used to resume pending plans + decided history on daemon restart).</summary>
    IReadOnlyList<PendingPlan> LoadAll();

    /// <summary>Upsert one plan (keyed by <see cref="PendingPlan.PlanId"/>).</summary>
    void Save(PendingPlan plan);
}

/// <summary>An in-memory <see cref="IPlanApprovalStore"/> for the pure/unit paths.</summary>
public sealed class InMemoryPlanApprovalStore : IPlanApprovalStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPlan> _plans = new(StringComparer.Ordinal);

    public IReadOnlyList<PendingPlan> LoadAll()
    {
        lock (_gate)
        {
            return _plans.Values.OrderBy(p => p.DraftedAt).ToList();
        }
    }

    public void Save(PendingPlan plan)
    {
        lock (_gate)
        {
            _plans[plan.PlanId] = plan;
        }
    }
}

/// <summary>
/// A JSON-file <see cref="IPlanApprovalStore"/> (mirrors <c>LeaderRegistry</c>'s durable pattern — no EF
/// migration needed). Two service instances over the same path prove restart-safety (test 3): the second
/// instance rehydrates every drafted + decided plan, approver identity intact.
/// </summary>
public sealed class JsonPlanApprovalStore : IPlanApprovalStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public JsonPlanApprovalStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    public IReadOnlyList<PendingPlan> LoadAll()
    {
        lock (_gate)
        {
            return LoadDtosLocked().Select(FromDto).OrderBy(p => p.DraftedAt).ToList();
        }
    }

    public void Save(PendingPlan plan)
    {
        lock (_gate)
        {
            var dtos = LoadDtosLocked();
            dtos.RemoveAll(d => d.PlanId == plan.PlanId);
            dtos.Add(ToDto(plan));
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Write-rename so a crash mid-write never leaves a torn file.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dtos));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private List<PlanDto> LoadDtosLocked()
    {
        if (!File.Exists(_path))
        {
            return new();
        }

        try
        {
            return JsonSerializer.Deserialize<List<PlanDto>>(File.ReadAllText(_path)) ?? new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    private static PlanDto ToDto(PendingPlan p) => new()
    {
        PlanId = p.Plan.PlanId,
        CoordinatorId = p.CoordinatorId,
        Title = p.Plan.Title,
        Scope = p.Plan.Scope.ToList(),
        Approach = p.Plan.Approach,
        TestStrategy = p.Plan.TestStrategy,
        TaskPrompt = p.TaskPrompt,
        BudgetUsd = p.Plan.BudgetUsd,
        DraftedAt = p.Plan.DraftedAt,
        Status = p.Status.ToString(),
        ApproverIdentity = p.ApproverIdentity,
        DecidedAt = p.DecidedAt,
    };

    private static PendingPlan FromDto(PlanDto d) => new(
        new TaskPlan(d.PlanId, d.Title, d.Scope ?? new List<string>(), d.Approach ?? "", d.TestStrategy ?? "", d.BudgetUsd, d.DraftedAt),
        d.CoordinatorId,
        d.TaskPrompt ?? "",
        Enum.TryParse<PlanStatus>(d.Status, out var s) ? s : PlanStatus.Pending,
        d.ApproverIdentity,
        d.DecidedAt);

    private sealed class PlanDto
    {
        public string PlanId { get; set; } = "";
        public string CoordinatorId { get; set; } = "";
        public string Title { get; set; } = "";
        public List<string>? Scope { get; set; }
        public string? Approach { get; set; }
        public string? TestStrategy { get; set; }
        public string? TaskPrompt { get; set; }
        public decimal BudgetUsd { get; set; }
        public DateTimeOffset DraftedAt { get; set; }
        public string Status { get; set; } = "Pending";
        public string? ApproverIdentity { get; set; }
        public DateTimeOffset? DecidedAt { get; set; }
    }
}

/// <summary>
/// P2-14 plan-approval service (contract §2). Owns the pending-plan queue, the approve/reject decisions,
/// the daemon-derived approver identity persisted with the plan, restart-safety, and the S-8
/// anti-approval-fatigue caps. <b>A pending plan consumes no admission or budget</b> (S-8) — so drafting is
/// itself rate/count-limited here, and only an approved plan proceeds to the P2-09 spawn path (via the
/// <see cref="PlanApproved"/> event, admission + budget applied there).
/// </summary>
public sealed class PlanApprovalService
{
    private readonly IPlanApprovalStore _store;
    private readonly IAuditLog _audit;
    private readonly Func<DateTimeOffset> _clock;
    private readonly PlanApprovalOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<string, PendingPlan> _plans = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _draftTimestamps = new(StringComparer.Ordinal);

    public PlanApprovalService(
        IPlanApprovalStore? store = null,
        IAuditLog? audit = null,
        Func<DateTimeOffset>? clock = null,
        PlanApprovalOptions? options = null)
    {
        _store = store ?? new InMemoryPlanApprovalStore();
        _audit = audit ?? new InMemoryAuditLog();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _options = options ?? new PlanApprovalOptions();

        // Restart resume: rehydrate every persisted plan (pending + decided).
        foreach (var plan in _store.LoadAll())
        {
            _plans[plan.PlanId] = plan;
        }
    }

    /// <summary>Raised (off any lock) after every draft/approve/reject so the gRPC stream / UI can re-read.</summary>
    public event Action? Changed;

    /// <summary>Raised when a plan is approved — the P2-09 spawn path subscribes (admission + budget apply there).</summary>
    public event Action<PendingPlan>? PlanApproved;

    /// <summary>
    /// Drafts a pending plan from validated <paramref name="fields"/> (the coordinator's <c>spawn_worker</c>
    /// tool lands here). Enforces the S-8 caps: the per-coordinator concurrent <c>PlanPending</c> ceiling and
    /// the drafting rate limit. Excess → <see cref="DraftOutcome.ResourceExhausted"/> + a
    /// <c>plan_draft_rejected</c> audit event; nothing is persisted as pending.
    /// </summary>
    public DraftResult Draft(string coordinatorId, string title, TaskPlanFields fields, string taskPrompt, decimal budgetUsd)
    {
        if (string.IsNullOrWhiteSpace(coordinatorId))
        {
            throw new ArgumentException("coordinatorId is required.", nameof(coordinatorId));
        }

        ArgumentNullException.ThrowIfNull(fields);

        PendingPlan drafted;
        lock (_gate)
        {
            var pending = PendingCountLocked(coordinatorId);
            if (pending >= _options.MaxPendingPerCoordinator)
            {
                AuditDraftRejectedLocked(coordinatorId, "pending-cap", pending);
                return new DraftResult(DraftOutcome.ResourceExhausted,
                    $"Plan limit reached — {pending} drafts are already waiting on you. Decide those first.", null);
            }

            if (ExceedsDraftRateLocked(coordinatorId))
            {
                AuditDraftRejectedLocked(coordinatorId, "rate-limit", pending);
                return new DraftResult(DraftOutcome.ResourceExhausted,
                    "Drafting too fast — slow down; the human gate is the point.", null);
            }

            var plan = new TaskPlan(
                PlanId: Guid.NewGuid().ToString("N"),
                Title: string.IsNullOrWhiteSpace(title) ? "Untitled plan" : title,
                Scope: fields.Scope,
                Approach: fields.Approach,
                TestStrategy: fields.TestStrategy,
                BudgetUsd: budgetUsd,
                DraftedAt: _clock());

            drafted = new PendingPlan(plan, coordinatorId, taskPrompt ?? "", PlanStatus.Pending, null, null);
            _plans[drafted.PlanId] = drafted;
            RecordDraftTimestampLocked(coordinatorId);
            _store.Save(drafted);
        }

        Changed?.Invoke();
        return new DraftResult(DraftOutcome.Drafted, "Plan drafted — awaiting human approval.", drafted.PlanId);
    }

    /// <summary>
    /// Approves a pending plan. The <paramref name="approverIdentity"/> is resolved by the caller <b>from
    /// the daemon side</b> (the authenticated connection's OS peer credential) — this method takes no
    /// client-supplied identity and the proto exposes no such field (SA-1/F2). Records
    /// <c>(plan, approver, timestamp)</c>, persists, and raises <see cref="PlanApproved"/> so the spawn
    /// proceeds (admission + budget checked there).
    /// </summary>
    public PendingPlan Approve(string planId, string approverIdentity)
    {
        if (string.IsNullOrWhiteSpace(approverIdentity))
        {
            // Never record an empty approver — that would be an unattributable approval.
            throw new ArgumentException("approverIdentity is required (daemon-derived).", nameof(approverIdentity));
        }

        PendingPlan approved;
        lock (_gate)
        {
            var plan = GetPendingLocked(planId);
            approved = plan with
            {
                Status = PlanStatus.Approved,
                ApproverIdentity = approverIdentity,
                DecidedAt = _clock(),
            };
            _plans[planId] = approved;
            _store.Save(approved);
        }

        _audit.Append(new AuditEvent("plan_approved", new Dictionary<string, string>
        {
            ["plan_id"] = approved.PlanId,
            ["coordinator_id"] = approved.CoordinatorId,
            ["approver"] = approverIdentity,
            ["scope_count"] = approved.Plan.Scope.Count.ToString(),
        }));

        Changed?.Invoke();
        PlanApproved?.Invoke(approved);
        return approved;
    }

    /// <summary>Rejects a pending plan — nothing spawns, no worktree residue (edge row 1). Audited.</summary>
    public PendingPlan Reject(string planId, string reason)
    {
        PendingPlan rejected;
        lock (_gate)
        {
            var plan = GetPendingLocked(planId);
            rejected = plan with
            {
                Status = PlanStatus.Rejected,
                DecidedAt = _clock(),
            };
            _plans[planId] = rejected;
            _store.Save(rejected);
        }

        _audit.Append(new AuditEvent("plan_rejected", new Dictionary<string, string>
        {
            ["plan_id"] = rejected.PlanId,
            ["coordinator_id"] = rejected.CoordinatorId,
            ["reason"] = reason ?? "",
        }));

        Changed?.Invoke();
        return rejected;
    }

    /// <summary>Every plan (pending + decided), drafted-order.</summary>
    public IReadOnlyList<PendingPlan> All()
    {
        lock (_gate)
        {
            return _plans.Values.OrderBy(p => p.DraftedAt).ToList();
        }
    }

    /// <summary>The plans still awaiting a decision, optionally scoped to one coordinator.</summary>
    public IReadOnlyList<PendingPlan> Pending(string? coordinatorId = null)
    {
        lock (_gate)
        {
            return _plans.Values
                .Where(p => p.Status == PlanStatus.Pending)
                .Where(p => coordinatorId is null || p.CoordinatorId == coordinatorId)
                .OrderBy(p => p.DraftedAt)
                .ToList();
        }
    }

    /// <summary>The concurrent <c>PlanPending</c> count for one coordinator (the S-8 pressure signal source).</summary>
    public int PendingCount(string coordinatorId)
    {
        lock (_gate)
        {
            return PendingCountLocked(coordinatorId);
        }
    }

    /// <summary>The look-up for one plan (null when unknown).</summary>
    public PendingPlan? Get(string planId)
    {
        lock (_gate)
        {
            return _plans.TryGetValue(planId, out var p) ? p : null;
        }
    }

    /// <summary>
    /// The S-8 pressure signal for a coordinator: the "N plans pending" fact line surfaced on the
    /// coordinator surface, or null when there is no pressure worth showing (≤ the soft threshold of 2).
    /// </summary>
    public string? PressureSignal(string coordinatorId)
    {
        List<PendingPlan> pending;
        lock (_gate)
        {
            pending = _plans.Values
                .Where(p => p.Status == PlanStatus.Pending && p.CoordinatorId == coordinatorId)
                .OrderBy(p => p.DraftedAt)
                .ToList();
        }

        if (pending.Count <= 2)
        {
            return null;
        }

        var oldest = (int)Math.Max(0, (_clock() - pending[0].DraftedAt).TotalMinutes);
        return $"{pending.Count} plans pending — the oldest has waited {oldest} min.";
    }

    private int PendingCountLocked(string coordinatorId) =>
        _plans.Values.Count(p => p.Status == PlanStatus.Pending && p.CoordinatorId == coordinatorId);

    private PendingPlan GetPendingLocked(string planId)
    {
        if (!_plans.TryGetValue(planId, out var plan))
        {
            throw new InvalidOperationException($"No plan '{planId}'.");
        }

        if (plan.Status != PlanStatus.Pending)
        {
            throw new InvalidOperationException($"Plan '{planId}' is already {plan.Status} — decisions are final.");
        }

        return plan;
    }

    private bool ExceedsDraftRateLocked(string coordinatorId)
    {
        var now = _clock();
        var window = _options.DraftWindowOrDefault;
        if (!_draftTimestamps.TryGetValue(coordinatorId, out var stamps))
        {
            return false;
        }

        stamps.RemoveAll(t => now - t > window);
        return stamps.Count >= _options.MaxDraftsPerWindow;
    }

    private void RecordDraftTimestampLocked(string coordinatorId)
    {
        if (!_draftTimestamps.TryGetValue(coordinatorId, out var stamps))
        {
            stamps = new List<DateTimeOffset>();
            _draftTimestamps[coordinatorId] = stamps;
        }

        stamps.Add(_clock());
    }

    private void AuditDraftRejectedLocked(string coordinatorId, string cause, int pending)
    {
        _audit.Append(new AuditEvent("plan_draft_rejected", new Dictionary<string, string>
        {
            ["coordinator_id"] = coordinatorId,
            ["cause"] = cause,
            ["pending"] = pending.ToString(),
        }));
    }
}
