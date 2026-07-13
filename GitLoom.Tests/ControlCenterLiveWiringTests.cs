using System;
using GitLoom.App.Services;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Mock;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// P2-47 integration proof #2 — the "no mock services in the live control-center path" guard. It fails if
/// the shipped orchestrator bundle resolves any <see cref="MockOrchestrator"/> behind a control-center
/// seam. This is the anti-regression that keeps the P2-13 §0 acceptance true: the shipped app runs on the
/// real <see cref="DaemonBackedOrchestrator"/>, and the mock stays confined to the design render harness.
/// </summary>
public sealed class ControlCenterLiveWiringTests
{
    /// <summary>The shipped production bundle exposes the real DaemonClient-backed adapter behind every
    /// seam — never a mock.</summary>
    [Fact]
    public void ShippedBundle_HasNoMockBehindAnySeam()
    {
        var bundle = GitLoom.App.App.CreateProductionOrchestratorServices();
        try
        {
            AssertNotMock(bundle.Agents, nameof(bundle.Agents));
            AssertNotMock(bundle.Queue, nameof(bundle.Queue));
            AssertNotMock(bundle.Coordinator, nameof(bundle.Coordinator));
            AssertNotMock(bundle.Kill, nameof(bundle.Kill));
            AssertNotMock(bundle.Telemetry, nameof(bundle.Telemetry));
            AssertNotMock(bundle.Vibe, nameof(bundle.Vibe));

            // Positive: it really is the daemon-backed adapter (the swap happened, not a silent fallback).
            Assert.IsType<DaemonBackedOrchestrator>(bundle.Agents);
        }
        finally
        {
            bundle.Owner?.Dispose();
        }
    }

    /// <summary>The default factory the app uses (<see cref="GitLoom.App.App.CreateOrchestratorServices"/>)
    /// yields the shipped, non-mock bundle — proving MainWindow's control center is not mock-backed.</summary>
    [Fact]
    public void DefaultFactory_IsProductionNonMock()
    {
        // Reset any harness override so this asserts the shipped default (harnesses mutate the static).
        GitLoom.App.App.OrchestratorServicesFactory = GitLoom.App.App.CreateProductionOrchestratorServices;

        var bundle = GitLoom.App.App.CreateOrchestratorServices();
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
