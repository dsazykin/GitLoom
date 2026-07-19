using System;
using System.Collections.Generic;
using System.Reflection;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Editions;

/// <summary>
/// The Pro edition (the shipped default) — the full agent platform. Its <see cref="CreateControlCenter"/>
/// routes through <see cref="App.CreateOrchestratorServices"/> (NOT <c>DaemonBackedOrchestrator.CreateBundle</c>
/// directly) so the headless render harnesses, which override <see cref="App.OrchestratorServicesFactory"/>
/// with a scripted <c>MockOrchestrator</c> before building <see cref="MainWindowViewModel"/>, still inject
/// their mock — the green-keeping contract that keeps behavior under this edition identical to today.
/// Kept in its own file: in Phase 2 this manifest moves to a Pro-only assembly (the contract types stay
/// in the shared shell).
/// </summary>
public sealed class ProManifest : IEditionManifest
{
    public string ProductName => "Mainguard Pro";

    public bool HasAgentPlatform => true;

    public EditionFirstRun FirstRun => EditionFirstRun.GitLoomOsProvisioning;

    public bool ShowsAgentRail => true;

    public IAgentPlatformSurface? CreateControlCenter()
        => new ControlCenterViewModel(App.CreateOrchestratorServices());

    // The Pro Tools surface (step 1c) — a single stateless instance holding the five moved command
    // bodies (and, with them, the Core.Agents + Pro-View references the shared hub no longer carries).
    public IProToolsSurface? ProTools { get; } = new ProToolsSurface();

    // The 7 current rail destinations, in order (the labels/icons mirror MainWindow.axaml). Defined
    // now; the hard-coded rail keeps rendering these until the data-driven rail lands in 1b.
    //
    // ContentViewModelType (1f): the four host tabs already render their section content through the
    // shell's ViewLocator today (MainWindow's HostSectionContent ContentControl → PullRequestsViewModel →
    // PullRequestsView, …), so each carries its ViewModel type and 1f's manifest-completeness test proves
    // every one resolves to a real View. Repo/Coordinator/Resources stay null ON PURPOSE: they are special
    // direct-panel content, NOT ViewLocator-routed (the repo workspace, the coordinator surface, and the
    // lazily-built resource monitor are bound directly, not via the …ViewModel→…View convention). Phase 2
    // populates the remaining three when section content routing converges on the ContentControl+ViewLocator path.
    public IReadOnlyList<RailSectionDescriptor> Sections { get; } = new RailSectionDescriptor[]
    {
        new("Repo",          "Repo viewer",   "CommitIcon",          false, RailAdornmentKind.None,      null),
        new("Coordinator",   "Coordinator",   "DiscussionIcon",      false, RailAdornmentKind.Attention, null),
        new("Resources",     "Resources",     "ResourceMonitorIcon", false, RailAdornmentKind.Spend,     null),
        new("PullRequests",  "Pull requests", "PullRequestIcon",     true,  RailAdornmentKind.None,      typeof(PullRequestsViewModel)),
        new("Issues",        "Issues",        "IssueIcon",           true,  RailAdornmentKind.None,      typeof(IssuesViewModel)),
        new("Notifications", "Notifications", "BellIcon",            true,  RailAdornmentKind.None,      typeof(NotificationsViewModel)),
        new("Releases",      "Releases",      "TagIcon",             true,  RailAdornmentKind.None,      typeof(ReleasesViewModel)),
    };

    public IReadOnlyList<SettingsPageDescriptor> SettingsPages { get; } = Array.Empty<SettingsPageDescriptor>();

    public IReadOnlyList<Assembly> ViewAssemblies { get; } = new[] { typeof(App).Assembly };
}
