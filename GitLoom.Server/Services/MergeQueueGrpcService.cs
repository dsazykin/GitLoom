using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Agents.Orchestrator;
using GitLoom.Protos.V1;
using GitLoom.Server.Logging;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="MergeQueueService"/> (P2-10). Validation + dispatch only — the state
/// machine, verification provenance, and the CanMerge gate all live in the daemon-side
/// <see cref="MergeQueue"/> resolved through the <see cref="IMergeQueueRegistry"/>. There is no
/// auto-merge RPC: <c>ConfirmMerge</c> only records the outcome of a merge the human already drove.
/// </summary>
public sealed class MergeQueueGrpcService : MergeQueueService.MergeQueueServiceBase
{
    private readonly IMergeQueueRegistry _registry;
    private readonly KillSwitchGate _killGate;
    private readonly IMergeBranchDiffService _mergeDiff;
    private readonly ILogger _log;

    public MergeQueueGrpcService(
        IMergeQueueRegistry registry, KillSwitchGate killGate, IMergeBranchDiffService mergeDiff,
        ILoggerFactory loggerFactory)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _killGate = killGate ?? throw new ArgumentNullException(nameof(killGate));
        _mergeDiff = mergeDiff ?? throw new ArgumentNullException(nameof(mergeDiff));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.Merge);
    }

    public override async Task StreamQueue(
        StreamQueueRequest request,
        IServerStreamWriter<QueueUpdate> responseStream,
        ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var queue = ctx.Queue;

        // Snapshot-then-deltas: push the current state, then re-push on every change until detach.
        using var signal = new SemaphoreSlim(0);
        void OnChanged() => signal.Release();
        queue.Changed += OnChanged;
        try
        {
            await responseStream.WriteAsync(Snapshot(queue)).ConfigureAwait(false);
            while (!context.CancellationToken.IsCancellationRequested)
            {
                await signal.WaitAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(Snapshot(queue)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client detached — normal teardown.
        }
        finally
        {
            queue.Changed -= OnChanged;
        }
    }

    public override async Task<RunVerificationResponse> RunVerification(RunVerificationRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var record = await ctx.Queue.RunVerificationAsync(request.AgentId, context.CancellationToken).ConfigureAwait(false);
        return new RunVerificationResponse
        {
            AgentId = record.AgentId,
            MainSha = record.MainSha,
            Passed = record.Passed,
            ResolvedCommand = record.ResolvedCommand,
            ConfigHash = record.ConfigHash,
            State = ctx.Queue.GetState(request.AgentId).ToString(),
        };
    }

    public override Task<CanMergeResponse> CanMerge(CanMergeRequest request, ServerCallContext context)
    {
        var ctx = Resolve(request.RepoHandle);
        var can = ctx.Queue.CanMerge(request.AgentId, out var reason);
        return Task.FromResult(new CanMergeResponse { CanMerge = can, Reason = reason });
    }

    public override Task<BeginMergeResponse> BeginMerge(BeginMergeRequest request, ServerCallContext context)
    {
        // SA-1/F4: while the kill switch holds the queue frozen, no merge may begin — loudly.
        ThrowIfFrozen("BeginMerge");
        var ctx = Resolve(request.RepoHandle);
        var leaseId = Guid.NewGuid().ToString("N");
        var verified = ctx.Queue.CurrentMainSha;
        var lease = ctx.Leases.TryBegin(request.RepoHandle, leaseId, request.AgentId, verified, "main");
        _log.LogInformation("BeginMerge repo={Repo} agent={Agent} granted={Granted}",
            request.RepoHandle, request.AgentId, lease is not null);
        return Task.FromResult(lease is null
            ? new BeginMergeResponse { Granted = false, Reason = "another merge is already in progress for this repository" }
            : new BeginMergeResponse { Granted = true, LeaseId = lease.LeaseId });
    }

    public override Task<ConfirmMergeResponse> ConfirmMerge(ConfirmMergeRequest request, ServerCallContext context)
    {
        // SA-1/F4: a frozen queue refuses the merge confirmation too.
        ThrowIfFrozen("ConfirmMerge");
        var ctx = Resolve(request.RepoHandle);
        // Record the idempotency outcome, then move the branch to Merged and fire the stale cascade.
        ctx.Leases.Confirm(request.RepoHandle, request.LeaseId, request.NewMainSha);
        ctx.Queue.ConfirmHumanMerge(request.AgentId, request.NewMainSha);
        _log.LogInformation("ConfirmMerge repo={Repo} agent={Agent} newMainSha={Sha}",
            request.RepoHandle, request.AgentId, request.NewMainSha);
        return Task.FromResult(new ConfirmMergeResponse { Confirmed = true });
    }

    public override Task<GetMergeDiffResponse> GetMergeDiff(GetMergeDiffRequest request, ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.RepoHandle) || string.IsNullOrWhiteSpace(request.AgentId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "repo_handle and agent_id are required."));
        }

        try
        {
            var diff = _mergeDiff.Compute(request.RepoHandle, request.AgentId);
            return Task.FromResult(new GetMergeDiffResponse
            {
                Branch = diff.Branch,
                MainBranch = diff.MainBranch,
                UnifiedDiff = diff.UnifiedDiff,
            });
        }
        catch (Mainguard.Git.Exceptions.RepoProvisioningException ex)
        {
            // No provisioned mirror / no such branch — a typed NOT_FOUND rather than an opaque Internal.
            throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
        }
    }

    private void ThrowIfFrozen(string operation)
    {
        if (_killGate.IsFrozen)
        {
            _log.LogWarning("{Operation} refused: merge queue frozen (kill switch engaged)", operation);
            throw new RpcException(new Status(StatusCode.FailedPrecondition,
                $"The merge queue is frozen (kill switch engaged) — {operation} is refused. Resume first."));
        }
    }

    private MergeQueueContext Resolve(string repoHandle)
    {
        return _registry.Resolve(repoHandle)
            ?? throw new RpcException(new Status(StatusCode.NotFound,
                $"No active merge queue for repo handle '{repoHandle}'."));
    }

    private static QueueUpdate Snapshot(MergeQueue queue)
    {
        var update = new QueueUpdate { MainSha = queue.CurrentMainSha };
        foreach (var agentId in queue.Agents)
        {
            var can = queue.CanMerge(agentId, out var reason);
            update.Entries.Add(new QueueEntry
            {
                AgentId = agentId,
                State = queue.GetState(agentId).ToString(),
                VerifiedMainSha = queue.CurrentMainSha,
                CanMerge = can,
                GateReason = reason,
                // P2-13 carried-in from P2-12: badge external-PR intake entries as such.
                Origin = queue.GetOrigin(agentId).ToString(),
            });
        }

        return update;
    }
}
