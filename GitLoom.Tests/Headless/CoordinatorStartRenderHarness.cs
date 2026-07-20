using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Services;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Xunit;

namespace GitLoom.Tests.Headless;

/// <summary>
/// PR3 — the "Start coordinator" card (the new visible surface) rendered in EVERY one of the five
/// themes for human visual review: the CLI picker + primary action + explainer in its rest state,
/// and the live-coordinator fact line + "Open terminal" in the started state. PNGs land in
/// artifacts_headless/.
/// </summary>
public class CoordinatorStartRenderHarness
{
    private static readonly string[] ThemeKeys =
        { "MidnightLoom", "DaylightLoom", "CommandDeck", "Atelier", "LoomAurora" };

    [AvaloniaFact]
    public async Task CoordinatorStartCard_HeadlessPng_AllThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);

            using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
            using var vm = new ControlCenterViewModel(new OrchestratorServices(
                new RenderCliHost(), mock, mock, mock, mock, mock, Owner: null));
            await vm.LoadInstalledClisAsync();

            Assert.True(vm.CanStartCoordinator); // the card is what this harness exists to show

            var win = new Window
            {
                Width = 1280,
                Height = 800,
                Content = new ControlCenterView { DataContext = vm },
            };
            win.Show();
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"coordinator_start_{theme}.png"));
            win.Content = null;
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    [AvaloniaFact]
    public async Task CoordinatorLiveFactLine_HeadlessPng_DefaultTheme()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        using var mock = new MockOrchestrator(TimeSpan.FromHours(1));
        var host = new RenderCliHost();
        using var vm = new ControlCenterViewModel(new OrchestratorServices(
            host, mock, mock, mock, mock, mock, Owner: null));
        await vm.LoadInstalledClisAsync();
        await vm.StartCoordinatorCommand.ExecuteAsync(null);
        vm.FocusCoordinator(); // back to the coordinator surface — the live fact line shows

        Assert.True(vm.IsCoordinatorLive);

        var win = new Window
        {
            Width = 1280,
            Height = 800,
            Content = new ControlCenterView { DataContext = vm },
        };
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "coordinator_live_factline.png"));
        win.Content = null;
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(30);
        }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }

    /// <summary>A representative CLI host for the design render (two CLIs, instant start).</summary>
    private sealed class RenderCliHost : IAgentService, ICliAgentHost
    {
        private readonly List<AgentInfo> _agents = new();

        public string? CoordinatorAgentId { get; private set; }

        public Task<IReadOnlyList<InstalledCliOption>> ListInstalledClisAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<InstalledCliOption>>(new[]
            {
                new InstalledCliOption("claude-code", "2.1.0", "ANTHROPIC_API_KEY"),
                new InstalledCliOption("opencode", "1.4.2", ""),
            });

        public Task<string> StartCoordinatorAsync(InstalledCliOption cli, CancellationToken ct)
        {
            CoordinatorAgentId = "coord-1";
            _agents.Add(new AgentInfo("coord-1", cli.Id, "agent/coord-1",
                AgentLifecycleState.Working, "planning", DateTimeOffset.UtcNow, AgentRoles.Coordinator));
            return Task.FromResult("coord-1");
        }

        public IReadOnlyList<AgentInfo> ListAgents() => _agents.ToArray();

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
