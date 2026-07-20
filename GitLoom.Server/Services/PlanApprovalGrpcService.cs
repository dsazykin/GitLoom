using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Protos.V1;
using GitLoom.Server.Auth;
using GitLoom.Server.Logging;
using Grpc.Core;
using Microsoft.Extensions.Logging;

// NOTE: Mainguard.Agents.Agents.Orchestrator is deliberately NOT imported — its PlanApprovalService collides
// with the proto-generated PlanApprovalService. The Core service is referenced fully-qualified below.
namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="PlanApprovalService"/> (P2-14). Validation + dispatch only — the pending
/// queue, the S-8 caps, the persistence, and the approval record live in the daemon-side
/// <see cref="Mainguard.Agents.Agents.Orchestrator.PlanApprovalService"/>.
///
/// <para><b>SA-1/F2 (binding):</b> <see cref="ApprovePlan"/> takes only a <c>plan_id</c>. The approver
/// identity is resolved <b>daemon-side</b> from the authenticated connection via
/// <see cref="IApproverIdentityResolver"/> — the request carries no identity field, so a client cannot
/// influence the recorded approver (test 11).</para>
/// </summary>
public sealed class PlanApprovalGrpcService : PlanApprovalService.PlanApprovalServiceBase
{
    private readonly Mainguard.Agents.Agents.Orchestrator.PlanApprovalService _plans;
    private readonly IApproverIdentityResolver _identity;
    private readonly ILogger _log;

    public PlanApprovalGrpcService(
        Mainguard.Agents.Agents.Orchestrator.PlanApprovalService plans,
        IApproverIdentityResolver identity,
        ILoggerFactory loggerFactory)
    {
        _plans = plans ?? throw new ArgumentNullException(nameof(plans));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Approval);
    }

    public override async Task StreamPlans(
        StreamPlansRequest request, IServerStreamWriter<PlanUpdate> responseStream, ServerCallContext context)
    {
        var coordinatorId = string.IsNullOrWhiteSpace(request.CoordinatorId) ? null : request.CoordinatorId;

        using var signal = new SemaphoreSlim(0);
        void OnChanged() => signal.Release();
        _plans.Changed += OnChanged;
        try
        {
            await responseStream.WriteAsync(Snapshot(coordinatorId)).ConfigureAwait(false);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(Snapshot(coordinatorId)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal teardown.
        }
        finally
        {
            _plans.Changed -= OnChanged;
        }
    }

    public override Task<ApprovePlanResponse> ApprovePlan(ApprovePlanRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "plan_id is required."));
        }

        // SA-1/F2: the approver is the connection's OS peer credential — NEVER anything in the request.
        var approver = _identity.Resolve(context);
        try
        {
            var approved = _plans.Approve(request.PlanId, approver);
            _log.LogInformation("ApprovePlan plan={Plan} approver={Approver}", request.PlanId, approver);
            return Task.FromResult(new ApprovePlanResponse
            {
                Approved = true,
                ApproverIdentity = approved.ApproverIdentity ?? approver,
            });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("ApprovePlan refused plan={Plan}: {Message}", request.PlanId, ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    public override Task<RejectPlanResponse> RejectPlan(RejectPlanRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.PlanId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "plan_id is required."));
        }

        try
        {
            _plans.Reject(request.PlanId, request.Reason ?? "");
            _log.LogInformation("RejectPlan plan={Plan}", request.PlanId);
            return Task.FromResult(new RejectPlanResponse { Rejected = true });
        }
        catch (InvalidOperationException ex)
        {
            _log.LogWarning("RejectPlan refused plan={Plan}: {Message}", request.PlanId, ex.Message);
            throw new RpcException(new Status(StatusCode.FailedPrecondition, ex.Message));
        }
    }

    private PlanUpdate Snapshot(string? coordinatorId)
    {
        var update = new PlanUpdate();
        foreach (var plan in _plans.All().Where(p => coordinatorId is null || p.CoordinatorId == coordinatorId))
        {
            update.Plans.Add(new PlanEntry
            {
                PlanId = plan.PlanId,
                CoordinatorId = plan.CoordinatorId,
                Title = plan.Title,
                Approach = plan.Plan.Approach,
                TestStrategy = plan.Plan.TestStrategy,
                Status = plan.Status.ToString(),
                BudgetUsd = (double)plan.BudgetUsd,
                ApproverIdentity = plan.ApproverIdentity ?? "",
            });
            update.Plans[^1].Scope.AddRange(plan.Plan.Scope);
        }

        update.PressureSignal = coordinatorId is not null ? _plans.PressureSignal(coordinatorId) ?? "" : "";
        return update;
    }
}
