using Mainguard.Agents.Agents;
using Mainguard.Agents.UI.Services;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// The daemon → client agent-state vocabulary (G-14 sends free-form strings). "Starting" (spawn
/// record created, jail still provisioning) and "Stopped" (session removed) are daemon words with
/// no same-named enum member — "Stopped" falling into the Working default was the ghost-coordinator
/// field bug (2026-07-22): a torn-down coordinator projected as alive forever, so the surface spun
/// on its startup loader and Stop looked like a no-op.
/// </summary>
public class DaemonStateMappingTests
{
    [Theory]
    [InlineData("Working", AgentLifecycleState.Working)]
    [InlineData("Paused", AgentLifecycleState.Paused)]
    [InlineData("Dead", AgentLifecycleState.Dead)]
    [InlineData("dead", AgentLifecycleState.Dead)] // enum names parse case-insensitively
    [InlineData("Starting", AgentLifecycleState.Provisioning)] // daemon word: record created, jail pending
    [InlineData("Stopped", AgentLifecycleState.TornDown)]      // daemon word: session removed — terminal!
    public void DaemonStateStrings_MapToTheHonestLifecycleState(string wire, AgentLifecycleState expected)
        => Assert.Equal(expected, DaemonBackedOrchestrator.MapState(wire));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SomethingNewer")]
    public void UnknownStates_StayConservativelyLive(string? wire)
        => Assert.Equal(AgentLifecycleState.Working, DaemonBackedOrchestrator.MapState(wire));
}
