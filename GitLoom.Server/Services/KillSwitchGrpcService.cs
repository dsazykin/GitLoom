using System;
using System.Linq;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Orchestrator;
using GitLoom.Protos.V1;
using Grpc.Core;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="KillSwitchService"/> (P2-14). Validation + dispatch only — the
/// freeze-first ordering (SA-1/F4), the RT-D4 hard-ceiling fan-out timing, the journal snapshot, and the
/// RT-D3 audit-gap discipline all live in the daemon-side <see cref="KillSwitch"/>.
/// </summary>
public sealed class KillSwitchGrpcService : KillSwitchService.KillSwitchServiceBase
{
    private readonly KillSwitch _killSwitch;

    public KillSwitchGrpcService(KillSwitch killSwitch)
    {
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
    }

    public override async Task<EngageKillResponse> Engage(EngageKillRequest request, ServerCallContext context)
    {
        var report = await _killSwitch.EngageAsync(context.CancellationToken).ConfigureAwait(false);
        return new EngageKillResponse
        {
            KillEpochId = report.KillEpochId,
            QueueFrozen = report.QueueFrozen,
            AgentsYielded = report.Agents.Count(a => a.Outcome == KillAgentOutcome.Yielded),
            AgentsPaused = report.Agents.Count(a => a.Outcome is KillAgentOutcome.Paused or KillAgentOutcome.PauseFailed),
            DeadlineSeconds = report.Deadline.TotalSeconds,
        };
    }

    public override Task<ResumeKillResponse> Resume(ResumeKillRequest request, ServerCallContext context)
    {
        _killSwitch.Resume();
        return Task.FromResult(new ResumeKillResponse { Resumed = true });
    }
}
