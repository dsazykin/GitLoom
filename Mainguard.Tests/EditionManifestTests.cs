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
/// 1a edition-seam smoke (ADR-0001, docs/adr/0001-product-editions.md): the Client manifest composes
/// ZERO Pro orchestration — MainWindow's <c>ControlCenter</c> is null and the shell still constructs +
/// disposes cleanly — while the default Pro manifest still yields a live control center through the
/// mock-injection seam (the green-keeping contract). Runs under the headless app (<c>[AvaloniaFact]</c>)
/// because <see cref="MainWindowViewModel"/>'s ctor reads <c>App.Settings</c> and the repo DB exactly as
/// the shipped shell does at startup. 1f expands edition testing; this is the minimal proof that the
/// Client path constructs cleanly and the Pro path is untouched.
/// </summary>
public class EditionManifestTests
{
    [AvaloniaFact]
    public void MainWindowViewModel_UnderClientManifest_HasNullControlCenter_AndDoesNotThrow()
    {
        var originalEdition = Mainguard.App.Shell.App.Edition;
        try
        {
            Mainguard.App.Shell.App.Edition = new ClientManifest();

            // Constructing the shell under the Client edition must not throw and must build no control
            // center (no agent platform). `using` proves teardown is clean too.
            using var vm = new MainWindowViewModel(null);

            Assert.Null(vm.ControlCenter);
        }
        finally
        {
            Mainguard.App.Shell.App.Edition = originalEdition;
        }
    }

    // 1c edition-seam contract: the Pro manifest exposes a Pro Tools surface (ProToolsSurface); the
    // Client manifest exposes none. This is what makes the SHARED hub's five delegated Tools commands
    // no-op under Client (App.Edition.ProTools is null) while running the real dialogs under Pro. A plain
    // [Fact] — it inspects the manifest singletons directly, no headless app needed.
    [Fact]
    public void ProTools_IsPresentUnderPro_AndNullUnderClient()
    {
        Assert.NotNull(new ProManifest().ProTools);
        Assert.IsType<ProToolsSurface>(new ProManifest().ProTools);
        Assert.Null(new ClientManifest().ProTools);
    }

    [AvaloniaFact]
    public void MainWindowViewModel_UnderProManifest_HasControlCenter()
    {
        var originalEdition = Mainguard.App.Shell.App.Edition;
        var originalFactory = Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory;
        try
        {
            // Pro's CreateControlCenter routes through App.CreateOrchestratorServices — inject the
            // scripted mock behind that seam exactly as the render harnesses do, proving the
            // mock-injection seam still holds under the manifest indirection.
            Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory =
                () => OrchestratorServices.FromSingle(new MockOrchestrator());
            Mainguard.App.Shell.App.Edition = new ProManifest();

            using var vm = new MainWindowViewModel(null);

            Assert.NotNull(vm.ControlCenter);
        }
        finally
        {
            Mainguard.App.Shell.App.Edition = originalEdition;
            Mainguard.Agents.UI.Editions.ProComposition.OrchestratorServicesFactory = originalFactory;
        }
    }
}
