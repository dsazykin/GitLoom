using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.ViewModels.Agents;
using GitLoom.App.Views;
using GitLoom.App.Views.Agents;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Xunit;

namespace GitLoom.Tests.Headless;

// P2-13 test 5 (§5) / TI-P2-13.6: render the section rail — the "activity bar" — with its four
// scripted agents in EVERY one of the five themes (Daylight Loom included) for human visual review
// against ControlCenterDesign §0/§2/§4 (badge legibility, spacing, kill-switch prominence). Also
// captures the per-agent Dock.Avalonia workspace so the dock chrome gets a visual pass. PNGs land in
// artifacts_headless/.
public class ActivityBarRenderHarness
{
    private static readonly string[] ThemeKeys =
        { "MidnightLoom", "DaylightLoom", "CommandDeck", "Atelier", "LoomAurora" };

    [AvaloniaFact]
    public void ActivityBar_HeadlessPng_AllThemes()
    {
        // Design render: inject the scripted mock behind the shipped seam so the rail shows representative
        // agents (the shipped app runs the DaemonClient-backed bundle — P2-47). Explicit, outside the app path.
        GitLoom.App.App.OrchestratorServicesFactory = () => OrchestratorServices.FromSingle(new MockOrchestrator());

        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1420, Height = 920 };
            win.Show();
            Settle();

            Assert.True(vm.IsRailExpanded);
            // Agents isn't part of the slimmed IAgentPlatformSurface seam (2d); under the default Pro edition
            // the ControlCenter is the concrete VM, so cast to read the LIFO list.
            Assert.Equal(4, ((ControlCenterViewModel)vm.ControlCenter!).Agents.Count); // four scripted agents

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"activitybar_rail_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }

        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    [AvaloniaFact]
    public void AgentWorkspace_Dock_FlightDeck_And_ConversationDeck()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

        foreach (var (kind, name) in new[]
                 {
                     (WorkspaceLayoutKind.FlightDeck, "flightdeck"),
                     (WorkspaceLayoutKind.ConversationDeck, "conversationdeck"),
                 })
        {
            using var vm = new AgentWorkspaceViewModel(
                "loom-3", kind,
                terminal: "loom-3 $ pytest -q\n................  16 passed in 3.1s\nloom-3 $ ",
                diff: "agent diff — read-only\n+  def verify(self):\n+      return run_tests()",
                staging: "Staged\n  M  src/gateway.py\n  A  tests/test_budget.py");
            var win = new Window { Width = 1100, Height = 720, Content = new AgentWorkspaceView { DataContext = vm } };
            win.Show();
            Settle();
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"agent_workspace_{name}.png"));
            win.Content = null;
            HarnessHygiene.Teardown(win);
        }
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
}
