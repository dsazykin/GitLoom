using Mainguard.Agents.Agents;

namespace Mainguard.Server.Runtime;

/// <summary>
/// MG-2 server-side admission for the <b>wired</b> coordinator spawn shim
/// (<see cref="AgentSpawnService.HandleShimRequestAsync"/>). The in-jail <c>mainguard-agent spawn</c>
/// path previously reached <see cref="AgentSpawnService.SpawnAsync"/> with only the kill-gate check —
/// none of the caps that live in <see cref="Mainguard.Agents.Agents.Orchestrator.CoordinatorTools"/>
/// (the un-wired path) applied, so a coordinator agent could fan out unlimited Managed workers and
/// spawn under memory pressure. This evaluator re-applies the two hard, server-enforceable caps at the
/// wired admission point:
/// <list type="number">
/// <item>the active-Managed-worker ceiling (<see cref="Orchestrator.CoordinatorLimits.MaxActiveWorkers"/>), and</item>
/// <item>VM memory-headroom admission (<see cref="AdmissionController.CanSpawn"/>).</item>
/// </list>
/// <para>
/// The remaining MG-2 gate — requiring a matching human-<b>approved-plan</b> token — depends on the
/// plan-approval spawn pipeline being wired end-to-end (a coordinator drafting plans through a live
/// path, the daemon subscribing to <c>PlanApproved</c>, and role-scoped tokens); that is tracked with
/// MG-11/MG-12 and is not closed here.
/// </para>
/// </summary>
internal static class CoordinatorSpawnGate
{
    /// <summary>
    /// Returns a human-readable refusal reason, or <c>null</c> when the spawn is admitted. Order is
    /// cap-first (a deterministic, count-based refusal) then admission (the memory-pressure signal).
    /// </summary>
    internal static string? Evaluate(int activeManagedWorkers, int maxActiveWorkers, AdmissionController admission)
    {
        if (activeManagedWorkers >= maxActiveWorkers)
        {
            return $"Worker cap reached — {activeManagedWorkers}/{maxActiveWorkers} managed workers running. " +
                   "Let one finish before spawning another.";
        }

        if (!admission.CanSpawn(out var reason))
        {
            return reason;
        }

        return null;
    }
}
