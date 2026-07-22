using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.Services;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// PR3 — the control center's "Start coordinator" flow over fakes: the card gates on the CLI-host
/// seam + no live coordinator, the picker lists installed CLIs, a successful start shows the
/// coordinator's inline terminal (no per-agent workspace routing), Stop/Restart drive the session,
/// a refusal renders its honest message, and the exit guard's live-agent count reads off the same
/// projection.
/// </summary>
public class CoordinatorCliStartTests
{
    [AvaloniaFact]
    public async Task StartCoordinator_Spawns_MarksLive_AndShowsItsTerminalInline()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();
        Assert.True(vm.CanStartCoordinator);
        Assert.False(vm.IsCoordinatorLive);
        Assert.False(vm.ShowCoordinatorTerminal);
        Assert.Equal(2, vm.InstalledClis.Count);
        Assert.Equal("claude-code", vm.SelectedCli!.Id); // first installed preselected

        await vm.StartCoordinatorCommand.ExecuteAsync(null);

        Assert.Equal("claude-code", host.StartedWith!.Id); // the picked CLI is what spawned
        Assert.True(vm.IsCoordinatorLive);
        Assert.False(vm.CanStartCoordinator);
        Assert.Equal("", vm.CoordinatorStartError);

        // The coordinator's terminal is inline on the coordinator surface — NOT a per-agent workspace
        // document. Start keeps the coordinator focused and never routes through SelectAgent.
        Assert.True(vm.IsCoordinatorFocus);
        Assert.True(vm.ShowCoordinatorTerminal);
        Assert.Null(vm.SelectedAgentId);
        Assert.Null(vm.Workspace);
    }

    [AvaloniaFact]
    public async Task StopCoordinator_EndsTheSession_AndReturnsToStartable()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await vm.LoadInstalledClisAsync();

        await vm.StartCoordinatorCommand.ExecuteAsync(null);
        Assert.True(vm.IsCoordinatorLive);
        var startedId = host.CoordinatorAgentId!;

        await vm.StopCoordinatorCommand.ExecuteAsync(null);

        Assert.Contains(startedId, host.EndedAgentIds); // the live coordinator was ended
        Assert.False(vm.IsCoordinatorLive);
        Assert.False(vm.ShowCoordinatorTerminal);
        Assert.True(vm.CanStartCoordinator);            // startable again over the (now gone) session
    }

    [AvaloniaFact]
    public async Task RestartCoordinator_StopsTheOld_ThenSpawnsAFreshOne()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost();
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await vm.LoadInstalledClisAsync();

        await vm.StartCoordinatorCommand.ExecuteAsync(null);
        var firstId = host.CoordinatorAgentId!;
        Assert.Equal(1, host.StartCalls);

        await vm.RestartCoordinatorCommand.ExecuteAsync(null);

        Assert.Contains(firstId, host.EndedAgentIds);         // the old one was stopped
        Assert.Equal(2, host.StartCalls);                     // a fresh one was spawned
        Assert.True(vm.IsCoordinatorLive);
        Assert.NotEqual(firstId, host.CoordinatorAgentId);    // a new session id
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
    public async Task StartCoordinator_RpcFailure_ShowsTheDaemonsOwnDetail_NotTheEnvelope()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            StartFailure = new Grpc.Core.RpcException(new Grpc.Core.Status(
                Grpc.Core.StatusCode.FailedPrecondition,
                "Mainguard OS is missing the agent sandbox image (mainguard-agent-base) — it is "
                + "provisioned by setup; re-run Mainguard setup or rebuild the image, then try again.")),
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        await vm.LoadInstalledClisAsync();
        await vm.StartCoordinatorCommand.ExecuteAsync(null);

        Assert.Contains("sandbox image", vm.CoordinatorStartError);
        Assert.DoesNotContain("Status(", vm.CoordinatorStartError); // never the RpcException envelope
        Assert.True(vm.CanStartCoordinator); // still startable after the failure
    }

    [AvaloniaFact]
    public async Task LoadClis_DaemonDownAtStartup_RetriesUntilItAnswers_AndPopulates()
    {
        var previousDelay = ControlCenterViewModel.CliLoadRetryDelay;
        ControlCenterViewModel.CliLoadRetryDelay = TimeSpan.FromMilliseconds(10);
        try
        {
            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            // Down for the first three answers — the cold-boot / tier-1-restart window.
            var host = new FakeCliHost { ListFailuresRemaining = 3 };
            using var vm = new ControlCenterViewModel(BundleWith(host, mock));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await vm.LoadInstalledClisUntilAvailableAsync(cts.Token);

            Assert.True(host.ListCalls >= 4); // failed attempts + the answered one
            Assert.Equal(2, vm.InstalledClis.Count);
            Assert.Equal("", vm.CoordinatorStartError);
        }
        finally
        {
            ControlCenterViewModel.CliLoadRetryDelay = previousDelay;
        }
    }

    [AvaloniaFact]
    public async Task LoadClis_HonestEmptyAnswer_StopsRetrying()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost { Installed = Array.Empty<InstalledCliOption>() };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));
        await Task.Delay(200); // let the ctor's own retry loop land its single answered call

        var callsBefore = host.ListCalls;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await vm.LoadInstalledClisUntilAvailableAsync(cts.Token); // one answered call, then done
        await Task.Delay(200);

        Assert.Equal(callsBefore + 1, host.ListCalls); // an honest answer ends the retrying
        Assert.Contains("Settings", vm.CoordinatorStartError);
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
    public void CoordinatorRole_GatesTheCard_AndStaysOffTheWorkersRail()
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
        Assert.False(vm.IsCoordinatorDead);
        Assert.False(vm.CanStartCoordinator);

        // The coordinator is its own entity, owned by the coordinator surface — NEVER a row among
        // the worker agents. The rail carries workers only, with the quiet role word.
        var worker = Assert.Single(vm.Agents);
        Assert.Equal("w1", worker.AgentId);
        Assert.Equal("subagent", worker.RoleLabel);
        Assert.Equal("", new AgentRowViewModel(Agent("m1", AgentLifecycleState.Working)).RoleLabel);

        // The exit guard still counts the LIVE coordinator (it is a live agent in the VM).
        Assert.Equal(2, vm.LiveAgentCount);
    }

    [AvaloniaFact]
    public void DeadCoordinator_IsHonest_UngatesStart_AndStillShowsItsTerminal()
    {
        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new FakeCliHost
        {
            Agents = { Agent("c1", AgentLifecycleState.Dead, AgentRoles.Coordinator) },
        };
        using var vm = new ControlCenterViewModel(BundleWith(host, mock));

        // Honest death: a NEW coordinator is startable over the corpse, and the dead coordinator
        // neither counts as live nor rides the workers rail.
        Assert.False(vm.IsCoordinatorLive);
        Assert.True(vm.IsCoordinatorDead);
        Assert.True(vm.CanStartCoordinator);
        Assert.Empty(vm.Agents);
        Assert.Equal(0, vm.LiveAgentCount);

        // Its terminal region still shows — the daemon keeps the bound session's replay, so the terminal
        // shows the CLI's final output (the why of the death). Behind the fake host there's no live PTY,
        // so the surface renders the terminal placeholder rather than a wired terminal VM.
        Assert.True(vm.ShowCoordinatorTerminal);
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

        /// <summary>When &gt; 0, the next list calls fail with <see cref="ListFailure"/> (or a
        /// generic fault) and decrement — models a daemon that is down during app startup and
        /// comes back (the cold-boot / tier-1-restart race the retry loop exists for).</summary>
        public int ListFailuresRemaining { get; set; }

        public int ListCalls { get; private set; }

        public InstalledCliOption? StartedWith { get; private set; }

        /// <summary>How many times the coordinator was spawned (Start + each Restart's start leg).</summary>
        public int StartCalls { get; private set; }

        /// <summary>Every agent id passed to <see cref="EndAgentAsync"/> (Stop + Restart's stop leg).</summary>
        public List<string> EndedAgentIds { get; } = new();

        // ---- ICliAgentHost ----

        public string? CoordinatorAgentId { get; private set; }

        public Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct)
        {
            ListCalls++;
            if (ListFailuresRemaining > 0)
            {
                ListFailuresRemaining--;
                return Task.FromException<IReadOnlyList<InstalledCliOption>>(
                    ListFailure ?? new Grpc.Core.RpcException(
                        new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "connection refused")));
            }

            return ListFailure is null
                ? Task.FromResult(Installed)
                : Task.FromException<IReadOnlyList<InstalledCliOption>>(ListFailure);
        }

        public Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
        {
            if (StartFailure is not null)
            {
                throw StartFailure;
            }

            StartCalls++;
            StartedWith = cli;
            var id = $"coord-{StartCalls}";
            CoordinatorAgentId = id;
            Agents.Add(new AgentInfo(id, cli.Id, $"agent/{id}",
                AgentLifecycleState.Working, "", DateTimeOffset.UtcNow, AgentRoles.Coordinator));
            return Task.FromResult(id);
        }

        // ---- IAgentService ----

        public IReadOnlyList<AgentInfo> ListAgents() => Agents.ToArray();

        public event Action<AgentEvent>? EventReceived
        {
            add { }
            remove { }
        }

        /// <summary>Models the daemon tearing the session down: the agent leaves the list, so the VM's
        /// projection flips out of the live state.</summary>
        public Task EndAgentAsync(string agentId)
        {
            EndedAgentIds.Add(agentId);
            Agents.RemoveAll(a => a.AgentId == agentId);
            if (CoordinatorAgentId == agentId) CoordinatorAgentId = null;
            return Task.CompletedTask;
        }

        public Task PauseAgentAsync(string agentId) => Task.CompletedTask;

        public Task ResumeAgentAsync(string agentId) => Task.CompletedTask;

        public Task SendPromptAsync(string agentId, string prompt) => Task.CompletedTask;

        public IReadOnlyList<string> GetQueuedPrompts(string agentId) => Array.Empty<string>();

        public Task CancelQueuedPromptAsync(string agentId, int index) => Task.CompletedTask;

        public IReadOnlyList<string> GetTerminalTail(string agentId) => Array.Empty<string>();

        public IReadOnlyList<(string Step, bool Done)> GetPlanTree(string agentId) => Array.Empty<(string, bool)>();
    }
}
