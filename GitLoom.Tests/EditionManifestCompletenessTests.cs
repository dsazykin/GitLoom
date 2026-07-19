using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GitLoom.App;
using GitLoom.App.Editions;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// 1f manifest-completeness guard (ADR-0001, docs/adr/0001-product-editions.md): for each edition manifest,
/// EVERY rail section that declares a <c>ContentViewModelType</c> must resolve to a REAL View through the
/// shell's <see cref="ViewLocator"/> — never the honest "Not Found: …" placeholder. That is what stops a
/// manifest from listing a section whose parallel-named View doesn't exist (a broken destination that would
/// render the Not-Found TextBlock at runtime): give a bad section a <c>ContentViewModelType</c> whose
/// <c>…ViewModel→…View</c> transform names no type in the manifest's ViewAssemblies and <c>Build</c> returns
/// that TextBlock, so the type-name assertion below fails.
///
/// Non-vacuous by construction: the four host tabs (PRs/Issues/Notifications/Releases) carry their ViewModel
/// type today because they already render via ViewLocator through MainWindow's <c>HostSectionContent</c>, so
/// each manifest has ≥1 populated section (asserted) and the loop always runs. Repo/Coordinator/Resources
/// are deliberately null (special direct-panel content, not ViewLocator-routed) and are skipped; Phase 2
/// populates them when section content routing converges on the ContentControl+ViewLocator path — at which
/// point their Views come under this same guard automatically.
/// </summary>
public class EditionManifestCompletenessTests
{
    // [AvaloniaFact] because ViewLocator.Build constructs Avalonia controls (the resolved View's
    // InitializeComponent, or the Not-Found TextBlock), which need the headless app + UI thread.
    [AvaloniaFact]
    public void ProManifest_EverySectionWithContent_ResolvesToARealView()
        => AssertEverySectionWithContentResolves(EditionManifests.Pro);

    [AvaloniaFact]
    public void ClientManifest_EverySectionWithContent_ResolvesToARealView()
        => AssertEverySectionWithContentResolves(EditionManifests.Client);

    private static void AssertEverySectionWithContentResolves(IEditionManifest manifest)
    {
        var savedAssemblies = ViewLocator.ViewAssemblies;
        try
        {
            // Resolve exactly as this edition's shell would — through the SAME composed search set App seeds
            // at startup (the shell's own assembly first, then the edition's contributed View assemblies).
            // Step 2e split the Pro Views into Mainguard.Agents.UI, so ProManifest.ViewAssemblies now lists
            // only that Pro assembly; the host-collab Views (PRs/Issues/…) still live in the shell, so a
            // bare manifest.ViewAssemblies would miss them. ComposeViewAssemblies is exactly what runtime
            // uses. Restored in the finally so the shared headless session's shell-only default is untouched.
            ViewLocator.ViewAssemblies = GitLoom.App.App.ComposeViewAssemblies(manifest);
            var locator = new ViewLocator();

            var populated = manifest.Sections.Where(s => s.ContentViewModelType != null).ToArray();

            // Non-vacuous: at least one section must actually declare content, or this test proves nothing.
            Assert.NotEmpty(populated);

            foreach (var section in populated)
            {
                var vmType = section.ContentViewModelType!;

                // The section VMs are workspace-scoped (their ctors take live host services), so we do NOT
                // build a real one. ViewLocator.Build keys ONLY off param.GetType(), so an uninitialized
                // instance of the exact VM type drives the same …ViewModel→…View resolution the shell runs —
                // without fabricating a repo/service graph just to name a type.
                var vmInstance = RuntimeHelpers.GetUninitializedObject(vmType);
                var view = locator.Build(vmInstance);

                var expectedViewFullName = vmType.FullName!.Replace("ViewModel", "View", StringComparison.Ordinal);
                Assert.NotNull(view);
                // The strong assertion: resolved to the EXACT parallel-named View — which by definition is
                // NOT the "Not Found: …" TextBlock the locator returns when no View type is found.
                Assert.Equal(expectedViewFullName, view!.GetType().FullName);
                Assert.IsNotType<TextBlock>(view);
            }
        }
        finally
        {
            ViewLocator.ViewAssemblies = savedAssemblies;
        }
    }
}
