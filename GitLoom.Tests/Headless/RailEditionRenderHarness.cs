using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Editions;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Xunit;

namespace GitLoom.Tests.Headless;

// 1b visual proof + green-keeping guard: render the section rail under BOTH editions so a reviewer can
// confirm the Pro rail is look-identical to the shipped one (the 7 destinations + the worker list +
// the kill switch) and the Client rail drops the agent-platform destinations (Coordinator/Resources)
// and the entire agent rail (list + kill switch).
//
// The whole test assembly runs with parallelization disabled (see HeadlessRenderCollection —
// [assembly: CollectionBehavior(DisableTestParallelization = true)]), so mutating the static
// App.Edition / App.OrchestratorServicesFactory here cannot race another test; both are additionally
// restored in a finally. Seeds 1f's twin harness.
public class RailEditionRenderHarness
{
    [AvaloniaFact]
    public void Capture_Rail_UnderBothEditions()
    {
        var savedEdition = GitLoom.App.App.Edition;
        var savedFactory = GitLoom.App.Editions.ProComposition.OrchestratorServicesFactory;
        try
        {
            // ---- Pro (shipped default): the full agent platform. Inject the scripted mock behind the
            // shipped seam so the rail shows representative agents, exactly like the design harnesses. ----
            GitLoom.App.App.Edition = new ProManifest();
            GitLoom.App.Editions.ProComposition.OrchestratorServicesFactory = () => OrchestratorServices.FromSingle(new MockOrchestrator());
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

            var proVm = new MainWindowViewModel();
            var proWin = new MainWindow { DataContext = proVm, Width = 1420, Height = 920 };
            proWin.Show();
            Settle();

            Assert.Equal(7, proVm.RailSections.Count);                         // the 7 shipped destinations
            Assert.Contains(proVm.RailSections, s => s.Id == "Coordinator");
            Assert.Contains(proVm.RailSections, s => s.Id == "Resources");
            Assert.True(proVm.ShowsAgentRail);                                 // worker list + kill switch present
            Assert.NotNull(proVm.ControlCenter);
            // Agents isn't part of the slimmed IAgentPlatformSurface seam (2d — the rail is reached only as
            // opaque AgentRailContent); under Pro the ControlCenter is the concrete VM, so cast to read it.
            Assert.Equal(4, ((ControlCenterViewModel)proVm.ControlCenter!).Agents.Count); // four scripted agents in the list
            proWin.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "rail_pro.png"));
            HarnessHygiene.Teardown(proWin);

            // ---- Client: the plain Git GUI, no agent platform. CreateControlCenter() returns null; the
            // rail offers only Repo + the four host tabs and the agent rail chrome is gated out. ----
            GitLoom.App.App.Edition = new ClientManifest();

            var clientVm = new MainWindowViewModel();
            var clientWin = new MainWindow { DataContext = clientVm, Width = 1420, Height = 920 };
            clientWin.Show();
            Settle();

            Assert.Equal(5, clientVm.RailSections.Count);                      // Repo + the 4 host tabs only
            Assert.DoesNotContain(clientVm.RailSections, s => s.Id == "Coordinator");
            Assert.DoesNotContain(clientVm.RailSections, s => s.Id == "Resources");
            Assert.False(clientVm.ShowsAgentRail);                             // no worker list, no kill switch
            Assert.Null(clientVm.ControlCenter);
            clientWin.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "rail_client.png"));
            HarnessHygiene.Teardown(clientWin);
        }
        finally
        {
            GitLoom.App.App.Edition = savedEdition;
            GitLoom.App.Editions.ProComposition.OrchestratorServicesFactory = savedFactory;
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
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
