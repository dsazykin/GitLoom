using System;
using System.Linq;
using System.Threading.Tasks;
using GitLoom.Core.Agents.Orchestrator;
using GitLoom.Protos.V1;
using GitLoom.Server.Logging;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace GitLoom.Server.Services;

/// <summary>
/// gRPC transport for <see cref="KillSwitchService"/> (P2-14). Validation + dispatch only — the
/// freeze-first ordering (SA-1/F4), the RT-D4 hard-ceiling fan-out timing, the journal snapshot, and the
/// RT-D3 audit-gap discipline all live in the daemon-side <see cref="KillSwitch"/>.
/// </summary>
public sealed class KillSwitchGrpcService : KillSwitchService.KillSwitchServiceBase
{
    private readonly KillSwitch _killSwitch;
    private readonly ILogger _log;

    public KillSwitchGrpcService(KillSwitch killSwitch, ILoggerFactory loggerFactory)
    {
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _log = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory)))
            .CreateLogger(DaemonLogCategories.KillSwitch);
    }

    public override async Task<EngageKillResponse> Engage(EngageKillRequest request, ServerCallContext context)
    {
        _log.LogWarning("Engage: freezing everything (kill switch requested)");
        var report = await _killSwitch.EngageAsync(context.CancellationToken).ConfigureAwait(false);
        var yielded = report.Agents.Count(a => a.Outcome == KillAgentOutcome.Yielded);
        var paused = report.Agents.Count(a => a.Outcome is KillAgentOutcome.Paused or KillAgentOutcome.PauseFailed);
        _log.LogWarning(
            "Engaged: epoch={Epoch} queueFrozen={Frozen} yielded={Yielded} paused={Paused} deadline={Deadline}s",
            report.KillEpochId, report.QueueFrozen, yielded, paused, report.Deadline.TotalSeconds);
        return new EngageKillResponse
        {
            KillEpochId = report.KillEpochId,
            QueueFrozen = report.QueueFrozen,
            AgentsYielded = yielded,
            AgentsPaused = paused,
            DeadlineSeconds = report.Deadline.TotalSeconds,
        };
    }

    public override Task<ResumeKillResponse> Resume(ResumeKillRequest request, ServerCallContext context)
    {
        _killSwitch.Resume();
        _log.LogInformation("Resume: unfrozen");
        return Task.FromResult(new ResumeKillResponse { Resumed = true });
    }
}
