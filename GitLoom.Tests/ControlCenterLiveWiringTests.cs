using System;
using GitLoom.App.Services;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-47 integration proof #2 — the "no mock AND no empty-stub services in the live control-center path"
/// guard. It fails if the shipped orchestrator bundle resolves any <see cref="MockOrchestrator"/> behind a
/// control-center seam, or if any seam is anything other than the live DaemonClient-backed adapter (a
/// silent fallback / a degraded stub type). This is the anti-regression that keeps the P2-13 §0 acceptance
/// true: the shipped app runs on the real <see cref="DaemonBackedOrchestrator"/>, and the mock stays
/// confined to the design render harness. The <b>behavioral</b> no-empty-stub proof — that each seam is a
/// live projection off a real daemon action, not a hardcoded empty — is
/// <c>AlphaControlCenterProjectionTests</c> in GitLoom.Server.Tests (merge/plan/kill/telemetry/coordinator).
/// </summary>
public sealed class ControlCenterLiveWiringTests
{
    /// <summary>The shipped production bundle exposes the real DaemonClient-backed adapter behind
    /// EVERY seam — never a mock, and never a degraded stand-in type on any single surface.</summary>
    [Fact]
    public void ShippedBundle_HasNoMockOrStubBehindAnySeam()
    {
        var bundle = GitLoom.App.Editions.ProComposition.CreateProduction();
        try
        {
            AssertNotMock(bundle.Agents, nameof(bundle.Agents));
            AssertNotMock(bundle.Queue, nameof(bundle.Queue));
            AssertNotMock(bundle.Coordinator, nameof(bundle.Coordinator));
            AssertNotMock(bundle.Kill, nameof(bundle.Kill));
            AssertNotMock(bundle.Telemetry, nameof(bundle.Telemetry));
            AssertNotMock(bundle.Vibe, nameof(bundle.Vibe));

            // Positive: EVERY seam is the live daemon-backed adapter — the swap happened for all of them,
            // not just the agent list (the merge/plan/kill/telemetry/coordinator surfaces are wired too).
            Assert.IsType<DaemonBackedOrchestrator>(bundle.Agents);
            Assert.IsType<DaemonBackedOrchestrator>(bundle.Queue);
            Assert.IsType<DaemonBackedOrchestrator>(bundle.Coordinator);
            Assert.IsType<DaemonBackedOrchestrator>(bundle.Kill);
            Assert.IsType<DaemonBackedOrchestrator>(bundle.Telemetry);
        }
        finally
        {
            bundle.Owner?.Dispose();
        }
    }

    /// <summary>The default factory the app uses (<see cref="GitLoom.App.Editions.ProComposition.CreateOrchestratorServices"/>)
    /// yields the shipped, non-mock bundle — proving MainWindow's control center is not mock-backed.</summary>
    [Fact]
    public void DefaultFactory_IsProductionNonMock()
    {
        // Reset any harness override so this asserts the shipped default (harnesses mutate the static).
        GitLoom.App.Editions.ProComposition.OrchestratorServicesFactory = GitLoom.App.Editions.ProComposition.CreateProduction;

        var bundle = GitLoom.App.Editions.ProComposition.CreateOrchestratorServices();
        try
        {
            AssertNotMock(bundle.Agents, nameof(bundle.Agents));
            Assert.IsType<DaemonBackedOrchestrator>(bundle.Agents);
        }
        finally
        {
            bundle.Owner?.Dispose();
        }
    }

    private static void AssertNotMock(object service, string seam)
    {
        Assert.NotNull(service);
        Assert.False(service is MockOrchestrator,
            $"Control-center seam '{seam}' resolved a MockOrchestrator in the shipped path (P2-47 guard).");
    }
}
