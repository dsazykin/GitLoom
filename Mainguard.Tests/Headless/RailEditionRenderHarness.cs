using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.UI.Editions;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

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
        var savedEdition = Mainguard.App.Shell.App.Edition;
        var savedFactory = Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory;
        try
        {
            // ---- Pro (shipped default): the full agent platform. Inject the scripted mock behind the
            // shipped seam so the rail shows representative agents, exactly like the design harnesses. ----
            Mainguard.App.Shell.App.Edition = new ProManifest();
            Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory = () => OrchestratorServices.FromSingle(new MockOrchestrator());
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);

            // Seed the locator as App.Initialize does for this edition — the bare Mainguard.UI default resolves
            // neither the shell rail nor the Pro agent rail (Mainguard.Agents.UI), so the rail's AgentRailContent
            // would otherwise render the "Not Found:" placeholder. Restored when the using scope closes.
            using (HarnessHygiene.SeedViewAssemblies(Mainguard.App.Shell.App.Edition))
            {
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
                HarnessHygiene.AssertNoUnresolvedViews(proWin);                    // no unresolved Pro rail/content
                proWin.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "rail_pro.png"));
                HarnessHygiene.Teardown(proWin);
            }

            // ---- Client: the plain Git GUI, no agent platform. CreateControlCenter() returns null; the
            // rail offers only Repo + the four host tabs and the agent rail chrome is gated out. ----
            Mainguard.App.Shell.App.Edition = new ClientManifest();

            using (HarnessHygiene.SeedViewAssemblies(Mainguard.App.Shell.App.Edition))
            {
                var clientVm = new MainWindowViewModel();
                var clientWin = new MainWindow { DataContext = clientVm, Width = 1420, Height = 920 };
                clientWin.Show();
                Settle();

                Assert.Equal(5, clientVm.RailSections.Count);                      // Repo + the 4 host tabs only
                Assert.DoesNotContain(clientVm.RailSections, s => s.Id == "Coordinator");
                Assert.DoesNotContain(clientVm.RailSections, s => s.Id == "Resources");
                Assert.False(clientVm.ShowsAgentRail);                             // no worker list, no kill switch
                Assert.Null(clientVm.ControlCenter);
                HarnessHygiene.AssertNoUnresolvedViews(clientWin);                 // no unresolved shell host Views
                clientWin.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "rail_client.png"));
                HarnessHygiene.Teardown(clientWin);
            }
        }
        finally
        {
            Mainguard.App.Shell.App.Edition = savedEdition;
            Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory = savedFactory;
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
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
