using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using Mainguard.Agents.Agents;
using Xunit;

namespace GitLoom.Tests.Headless;

// TI-P2-13.4: the Resource Monitor projects a gateway/telemetry sample stream into its VM state
// (per-agent rows + the CPU sparkline). A controlled fake feed drives it deterministically.
public class ResourceMonitorStreamTests
{
    [AvaloniaFact]
    public void ResourceMonitor_ShouldRenderGatewaySnapshotStream()
    {
        var telemetry = new FakeTelemetry();
        telemetry.Seed(
            current: new ResourceSample(DateTimeOffset.UtcNow, 42, 3.5, 1.25m),
            usage: new[]
            {
                new AgentResourceUsage("a", "Loom-1", "Working", false, 30, 1.2, 0.60m, "compiling"),
                new AgentResourceUsage("b", "Loom-2", "Verifying", false, 12, 0.8, 0.65m, "pytest"),
            });

        using var vm = new ResourceMonitorViewModel(new FakeAgents(), telemetry);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Contains("2 agents", vm.TotalsText);

        // A new sample with a third agent flows through the Sampled event into the rows.
        telemetry.Seed(
            current: new ResourceSample(DateTimeOffset.UtcNow, 55, 4.1, 1.40m),
            usage: new[]
            {
                new AgentResourceUsage("a", "Loom-1", "Working", false, 33, 1.3, 0.70m, "compiling"),
                new AgentResourceUsage("b", "Loom-2", "AwaitingReview", false, 5, 0.7, 0.70m, "waiting"),
                new AgentResourceUsage("c", "Loom-3", "Working", false, 20, 1.0, 0.10m, "planning"),
            });
        telemetry.RaiseSampled();
        Drain();

        Assert.Equal(3, vm.Rows.Count);
        Assert.True(vm.CpuPoints.Count > 0); // sparkline built from history
    }

    private static void Drain()
    {
        for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
    }

    private sealed class FakeTelemetry : ITelemetryService
    {
        private readonly List<ResourceSample> _history = new();
        private IReadOnlyList<AgentResourceUsage> _usage = Array.Empty<AgentResourceUsage>();
        public ResourceSample Current { get; private set; } = new(DateTimeOffset.UtcNow, 0, 0, 0);
        public IReadOnlyList<ResourceSample> History => _history;
        public event Action? Sampled;

        public void Seed(ResourceSample current, IReadOnlyList<AgentResourceUsage> usage)
        {
            Current = current;
            _history.Add(current);
            _usage = usage;
        }

        public void RaiseSampled() => Sampled?.Invoke();
        public IReadOnlyList<AgentResourceUsage> GetAgentUsage() => _usage;
        public IReadOnlyList<SandboxEvent> GetSandboxEvents(string? agentId = null) => Array.Empty<SandboxEvent>();
        public Task<SpendBudget> GetSpendBudgetAsync(System.Threading.CancellationToken ct = default) => Task.FromResult(SpendBudget.None);
        public Task SetSpendBudgetAsync(SpendBudget budget, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeAgents : IAgentService
    {
        public IReadOnlyList<AgentInfo> ListAgents() => Array.Empty<AgentInfo>();
        public event Action<AgentEvent>? EventReceived { add { } remove { } }
        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;
        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();
        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;
        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();
        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();
        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;
        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;
        public Task EndAgentAsync(string agentId) => Task.CompletedTask;
    }
}
