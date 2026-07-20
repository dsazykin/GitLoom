using System;

namespace Mainguard.Agents.Agents;

/// <summary>
/// The bundle of orchestration seams the control-center ViewModels consume (Lane E Part 3). Introduced by
/// P2-47 so <see cref="ControlCenterViewModel"/> depends on the interfaces — not the concrete
/// <see cref="Mock.MockOrchestrator"/> — which is what makes the "mock → real DaemonClient with zero View
/// changes" swap (P2-13 §0 acceptance) actually possible: the shipped app builds a DaemonClient-backed
/// bundle, the design render harness builds a mock-backed one, and both flow through the same VM.
/// </summary>
/// <param name="Owner">The single object to dispose when the surface closes (the mock, or the
/// DaemonClient-backed adapter). Null when the caller owns the services' lifetimes separately.</param>
public sealed record OrchestratorServices(
    IAgentService Agents,
    IMergeQueueService Queue,
    ICoordinatorService Coordinator,
    IKillSwitchService Kill,
    ITelemetryService Telemetry,
    IVibeService Vibe,
    IDisposable? Owner = null)
{
    /// <summary>Wraps one object that implements the whole seam set (the mock, or a unified adapter).</summary>
    public static OrchestratorServices FromSingle<T>(T all)
        where T : IAgentService, IMergeQueueService, ICoordinatorService, IKillSwitchService, ITelemetryService, IVibeService
        => new(all, all, all, all, all, all, all as IDisposable);
}
