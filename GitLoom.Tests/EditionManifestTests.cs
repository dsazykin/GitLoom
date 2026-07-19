using Avalonia.Headless.XUnit;
using GitLoom.App.Editions;
using GitLoom.App.ViewModels;
using GitLoom.Core.Agents;
using GitLoom.Core.Agents.Mock;
using Xunit;

namespace GitLoom.Tests;

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
        var originalEdition = GitLoom.App.App.Edition;
        try
        {
            GitLoom.App.App.Edition = EditionManifests.Client;

            // Constructing the shell under the Client edition must not throw and must build no control
            // center (no agent platform). `using` proves teardown is clean too.
            using var vm = new MainWindowViewModel(null);

            Assert.Null(vm.ControlCenter);
        }
        finally
        {
            GitLoom.App.App.Edition = originalEdition;
        }
    }

    // 1c edition-seam contract: the Pro manifest exposes a Pro Tools surface (ProToolsSurface); the
    // Client manifest exposes none. This is what makes the SHARED hub's five delegated Tools commands
    // no-op under Client (App.Edition.ProTools is null) while running the real dialogs under Pro. A plain
    // [Fact] — it inspects the manifest singletons directly, no headless app needed.
    [Fact]
    public void ProTools_IsPresentUnderPro_AndNullUnderClient()
    {
        Assert.NotNull(EditionManifests.Pro.ProTools);
        Assert.IsType<ProToolsSurface>(EditionManifests.Pro.ProTools);
        Assert.Null(EditionManifests.Client.ProTools);
    }

    [AvaloniaFact]
    public void MainWindowViewModel_UnderProManifest_HasControlCenter()
    {
        var originalEdition = GitLoom.App.App.Edition;
        var originalFactory = GitLoom.App.App.OrchestratorServicesFactory;
        try
        {
            // Pro's CreateControlCenter routes through App.CreateOrchestratorServices — inject the
            // scripted mock behind that seam exactly as the render harnesses do, proving the
            // mock-injection seam still holds under the manifest indirection.
            GitLoom.App.App.OrchestratorServicesFactory =
                () => OrchestratorServices.FromSingle(new MockOrchestrator());
            GitLoom.App.App.Edition = EditionManifests.Pro;

            using var vm = new MainWindowViewModel(null);

            Assert.NotNull(vm.ControlCenter);
        }
        finally
        {
            GitLoom.App.App.Edition = originalEdition;
            GitLoom.App.App.OrchestratorServicesFactory = originalFactory;
        }
    }
}
