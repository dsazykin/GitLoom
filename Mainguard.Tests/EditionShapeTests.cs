using System.Linq;
using Avalonia.Headless.XUnit;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Mainguard.Agents.UI.Editions;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.App.Shell.Editions;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;
using Xunit;

namespace Mainguard.Tests;

/// <summary>
/// 1f structural edition-shape guard (ADR-0001, docs/adr/0001-product-editions.md): construct the REAL
/// shell under each manifest and assert its whole edition SHAPE — the rail destinations, the agent-platform
/// gates, the control center, and the Pro Tools surface — not just the pixels the twin render harness
/// captures. Runs under the headless app (<c>[AvaloniaFact]</c>) because <see cref="MainWindowViewModel"/>'s
/// ctor reads <c>App.Settings</c> and the repo DB exactly as the shipped shell does at startup.
///
/// The Client test is the consolidated "no Pro under Client" guard (deliverable 4): under the Client
/// manifest the shell composes ZERO agent orchestration — mirroring <see cref="ControlCenterLiveWiringTests"/>'s
/// intent from the edition side. A null <c>ControlCenter</c> means <c>CreateControlCenter()</c> never ran,
/// so no <c>DaemonBackedOrchestrator</c> / <c>DaemonClient</c> bundle is ever built (that bundle is the only
/// thing that constructs them). The whole assembly runs non-parallel (HeadlessRenderCollection), so the
/// static <c>App.Edition</c> / <c>App.OrchestratorServicesFactory</c> mutations here cannot race; both are
/// restored in a finally.
/// </summary>
public class EditionShapeTests
{
    /// <summary>Consolidated no-Pro-under-Client guard: the Client shell is a plain Git GUI — its rail is
    /// exactly Repo + the four host tabs (no Coordinator/Resources), the agent rail is gated off, and NO
    /// agent orchestration is composed (control center null, Pro Tools null).</summary>
    [AvaloniaFact]
    public void ClientShell_HasPlainGitShape_AndComposesZeroProOrchestration()
    {
        var savedEdition = Mainguard.App.Shell.App.Edition;
        try
        {
            Mainguard.App.Shell.App.Edition = new ClientManifest();

            using var vm = new MainWindowViewModel(null);

            // The rail is exactly Repo + the four host tabs — no agent-platform destinations.
            var ids = vm.RailSections.Select(s => s.Id).ToArray();
            Assert.Equal(new[] { "Repo", "PullRequests", "Issues", "Notifications", "Releases" }, ids);
            Assert.DoesNotContain("Coordinator", ids);
            Assert.DoesNotContain("Resources", ids);

            // No agent-platform chrome and no agent-platform composition.
            Assert.False(vm.ShowsAgentRail);
            Assert.False(vm.HasAgentPlatform);
            Assert.Null(vm.ControlCenter);                 // ⇒ no DaemonBackedOrchestrator / DaemonClient built
            Assert.Null(Mainguard.App.Shell.App.Edition.ProTools); // the five hub Pro Tools commands no-op under Client
        }
        finally
        {
            Mainguard.App.Shell.App.Edition = savedEdition;
        }
    }

    /// <summary>The Pro shell keeps its full agent-platform shape: the rail carries the Coordinator and
    /// Resources destinations, the agent rail is shown, a control center is composed (behind the injected
    /// mock seam), and the Pro Tools surface is present.</summary>
    [AvaloniaFact]
    public void ProShell_HasFullAgentPlatformShape()
    {
        var savedEdition = Mainguard.App.Shell.App.Edition;
        var savedFactory = Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory;
        try
        {
            // Pro's CreateControlCenter routes through App.CreateOrchestratorServices — inject the scripted
            // mock behind that seam (exactly as the render harnesses do) so a control center is built
            // without a live daemon, proving the mock-injection seam still holds under the manifest.
            Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory =
                () => OrchestratorServices.FromSingle(new MockOrchestrator());
            Mainguard.App.Shell.App.Edition = new ProManifest();

            using var vm = new MainWindowViewModel(null);

            var ids = vm.RailSections.Select(s => s.Id).ToArray();
            Assert.Contains("Coordinator", ids);
            Assert.Contains("Resources", ids);

            Assert.True(vm.ShowsAgentRail);
            Assert.True(vm.HasAgentPlatform);
            Assert.NotNull(vm.ControlCenter);
            Assert.NotNull(Mainguard.App.Shell.App.Edition.ProTools);
        }
        finally
        {
            Mainguard.App.Shell.App.Edition = savedEdition;
            Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory = savedFactory;
        }
    }
}
