using System;
using System.Collections.Generic;
using System.Reflection;
using Mainguard.App.Shell.ViewModels;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.Editions;

/// <summary>
/// The Client edition — the plain Git GUI with NO agent platform. <see cref="CreateControlCenter"/>
/// returns <c>null</c> (zero Pro orchestration is constructed), the agent rail is hidden, and the rail
/// offers only the Git/host destinations (no Coordinator/Resources). Selected only by the
/// <c>GITLOOM_EDITION=client</c> hatch or a test; the shipped default stays <see cref="ProManifest"/>.
/// </summary>
public sealed class ClientManifest : IEditionManifest
{
    public string ProductName => "Mainguard";

    public bool HasAgentPlatform => false;

    public EditionFirstRun FirstRun => EditionFirstRun.ClientClone;

    public bool ShowsAgentRail => false;

    public IAgentPlatformSurface? CreateControlCenter() => null;

    // No agent platform → no Pro Tools surface. The shared hub's five Pro Tools commands see a null here
    // and no-op; the Tools/File menu items that trigger them are gated off by HasAgentPlatform.
    public IProToolsSurface? ProTools => null;

    // Git/host destinations only — no Coordinator, no Resources (those need the agent platform).
    //
    // ContentViewModelType (1f): the four host tabs carry their ViewModel type because they render through
    // the shell's ViewLocator today (HostSectionContent → …ViewModel → …View), and 1f's completeness test
    // proves each resolves to a real View under this manifest's ViewAssemblies. Repo stays null ON PURPOSE:
    // it is special direct-panel content (the repo workspace), NOT ViewLocator-routed. Phase 2 populates it
    // when section content routing converges on the ContentControl+ViewLocator path.
    public IReadOnlyList<RailSectionDescriptor> Sections { get; } = new RailSectionDescriptor[]
    {
        new("Repo",          "Repo viewer",   "CommitIcon",      false, RailAdornmentKind.None, null),
        new("PullRequests",  "Pull requests", "PullRequestIcon", true,  RailAdornmentKind.None, typeof(PullRequestsViewModel)),
        new("Issues",        "Issues",        "IssueIcon",       true,  RailAdornmentKind.None, typeof(IssuesViewModel)),
        new("Notifications", "Notifications", "BellIcon",        true,  RailAdornmentKind.None, typeof(NotificationsViewModel)),
        new("Releases",      "Releases",      "TagIcon",         true,  RailAdornmentKind.None, typeof(ReleasesViewModel)),
    };

    public IReadOnlyList<SettingsPageDescriptor> SettingsPages { get; } = Array.Empty<SettingsPageDescriptor>();

    public IReadOnlyList<Assembly> ViewAssemblies { get; } = new[] { typeof(App).Assembly };
}
