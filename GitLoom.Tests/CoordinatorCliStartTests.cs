using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using GitLoom.App.Services;
using GitLoom.App.ViewModels;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Mock;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// PR3 — the control center's "Start coordinator" flow over fakes: the card gates on the CLI-host
/// seam + no live coordinator, the picker lists installed CLIs, a successful start opens the
/// coordinator's terminal document (SelectAgent), a refusal renders its honest message, and the
/// exit guard's live-agent count reads off the same projection. [AvaloniaFact]: the VM builds a
/// Dock workspace on selection.
/// </summary>
public class CoordinatorCliStartTests
{
    [AvaloniaFact]
    public async Task StartCoordinator_Spawns_MarksLive_AndOpensItsTerminalDocument()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();
        Assert.True(vm.CanStartCoordinator);
        Assert.False(vm.IsCoordinatorLive);
        Assert.Equal(2, vm.InstalledClis.Count);
        Assert.Equal("claude-code", vm.SelectedCli!.Id); // first installed preselected

        await vm.StartCoordinatorCommand.ExecuteAsync(null);

        Assert.Equal("claude-code", host.StartedWith!.Id); // the picked CLI is what spawned
        Assert.True(vm.IsCoordinatorLive);
        Assert.False(vm.CanStartCoordinator);
        Assert.Equal("", vm.CoordinatorStartError);
        Assert.Equal("coord-1", vm.SelectedAgentId);      // its terminal document opened
        Assert.NotNull(vm.Workspace);
        Assert.Equal("coord-1", vm.Workspace!.AgentId);
    }

    [AvaloniaFact]
    public async Task StartCoordinator_Refusal_RendersTheHonestMessage_AndStaysStartable()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            StartFailure = new InvalidOperationException("No repo is provisioned for agents yet — open a repository first."),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();
        await vm.StartCoordinatorCommand.ExecuteAsync(null);

        Assert.Contains("No repo is provisioned", vm.CoordinatorStartError);
        Assert.False(vm.IsCoordinatorLive);
        Assert.True(vm.CanStartCoordinator);
    }

    [AvaloniaFact]
    public async Task EmptyCatalog_ExplainsWhereToInstall()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost { Installed = Array.Empty<InstalledCliOption>() };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();

        Assert.Empty(vm.InstalledClis);
        Assert.Contains("Settings", vm.CoordinatorStartError);
    }

    [AvaloniaFact]
    public async Task LoadClis_DaemonWithoutTheRpc_NamesTheVersionSkew_NotUnreachable()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            ListFailure = new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.Unimplemented, "unknown method")),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();

        Assert.Contains("older than this app", vm.CoordinatorStartError);
        Assert.DoesNotContain("could not reach", vm.CoordinatorStartError);
    }

    [AvaloniaFact]
    public async Task LoadClis_DaemonUnreachable_KeepsTheReconnectMessage()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            ListFailure = new Grpc.Core.RpcException(
                new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "connection refused")),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();

        Assert.Contains("could not reach its agent daemon", vm.CoordinatorStartError);
    }

    [AvaloniaFact]
    public void LiveAgentCount_CountsOnlyNonTerminalStates()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            Agents =
            {
                Agent("a1", AgentLifecycleState.Working),
                Agent("a2", AgentLifecycleState.AwaitingReview),
                Agent("a3", AgentLifecycleState.Dead),
                Agent("a4", AgentLifecycleState.Merged),
                Agent("a5", AgentLifecycleState.TornDown),
            },
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        Assert.Equal(2, vm.LiveAgentCount);
    }

    [AvaloniaFact]
    public void CoordinatorRole_InTheAgentList_GatesTheCard_AndLabelsTheRow()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            Agents =
            {
                Agent("c1", AgentLifecycleState.Working, AgentRoles.Coordinator),
                Agent("w1", AgentLifecycleState.Working, AgentRoles.Managed),
            },
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        Assert.True(vm.IsCoordinatorLive);
        Assert.False(vm.CanStartCoordinator);

        // The P2-13 rail rows carry the role as a quiet word: coordinator / subagent.
        Assert.Equal("coordinator", vm.Agents.Single(r => r.AgentId == "c1").RoleLabel);
        Assert.Equal("subagent", vm.Agents.Single(r => r.AgentId == "w1").RoleLabel);
        Assert.Equal("", new AgentRowViewModel(Agent("m1", AgentLifecycleState.Working)).RoleLabel);
    }

    [Fact]
    public void ApiKeyProviderMap_MapsKnownEnvVars_AndOnlyThose()
    {
        Assert.Equal("anthropic", ApiKeyProviderMap.ProviderForEnvVar("ANTHROPIC_API_KEY"));
        Assert.Equal("openai", ApiKeyProviderMap.ProviderForEnvVar("OPENAI_API_KEY"));
        Assert.Null(ApiKeyProviderMap.ProviderForEnvVar(""));       // interactive-login adapter
        Assert.Null(ApiKeyProviderMap.ProviderForEnvVar(null));
        Assert.Null(ApiKeyProviderMap.ProviderForEnvVar("SOME_OTHER_KEY"));
        Assert.Equal("llm_anthropic", ApiKeyProviderMap.KeystoreKeyFor("anthropic"));
    }

    // ---- helpers -----------------------------------------------------------

    private static AgentInfo Agent(string id, AgentLifecycleState state, string role = AgentRoles.Manual) =>
        new(id, id, $"agent/{id}", state, "", DateTimeOffset.UtcNow, role);

    /// <summary>Bundle: the fake CLI host behind the Agents seam, the slow-tick mock behind the rest.</summary>
    private static OrchestratorServices BundleWith(FakeCliHost host, MockOrchestrator mock) =>
        new(host, mock, mock, mock, mock, mock, Owner: null);

    private sealed class FakeCliHost : IAgentService, ICliAgentHost
    {
        public List<AgentInfo> Agents { get; } = new();

        public IReadOnlyList<InstalledCliOption> Installed { get; set; } = new[]
        {
            new InstalledCliOption("claude-code", "2.1.0", "ANTHROPIC_API_KEY"),
            new InstalledCliOption("opencode", "1.4.2", ""),
        };

        public Exception? StartFailure { get; set; }

        public Exception? ListFailure { get; set; }

        public InstalledCliOption? StartedWith { get; private set; }

        // ---- ICliAgentHost ----

        public string? CoordinatorAgentId { get; private set; }

        public Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct) =>
            ListFailure is null ? Task.FromResult(Installed) : Task.FromException<IReadOnlyList<InstalledCliOption>>(ListFailure);

        public Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            StartedWith = cli;
            CoordinatorAgentId = "coord-1";
            Agents.Add(new AgentInfo("coord-1", cli.Id, "agent/coord-1",
                AgentLifecycleState.Working, "", DateTimeOffset.UtcNow, AgentRoles.Coordinator));
            return Task.FromResult("coord-1");
        }

        // ---- IAgentService ----

        public IReadOnlyList<AgentInfo> ListAgents() => Agents.ToArray();

        public event Action<AgentEvent>? EventReceived
        {
            add { }
            remove { }
        }

        public Task EndAgentAsync(string agentId) => Task.CompletedTask;

        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;

        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;

        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;

        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();

        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;

        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();

        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();
    }
}
