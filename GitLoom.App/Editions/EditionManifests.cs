using System.Collections.Generic;
using GitLoom.App.ViewModels;

namespace GitLoom.App.Editions;

/// <summary>
/// The known editions as singletons. <see cref="App.Edition"/> defaults to <see cref="Pro"/>; the
/// <c>GITLOOM_EDITION=client</c> startup hatch (App.Initialize) or a test selects <see cref="Client"/>.
/// </summary>
public static class EditionManifests
{
    // Step 2e: the Pro manifest moved to Mainguard.Agents.UI and so cannot name the shell's own host-collab
    // ViewModels — Pull requests / Issues / Notifications / Releases stay in GitLoom.App (2f keeps them out
    // of the Pro assembly so the Client head needs no Pro reference). The shell owns those rail descriptors
    // and injects them DOWN into ProManifest's composition seam here, before any code reads
    // ProManifest.Sections: every path that picks an edition goes through this type, so its static ctor has
    // always run first. The Client manifest, still in the shell, lists the same four inline.
    internal static readonly IReadOnlyList<RailSectionDescriptor> HostRailSections = new RailSectionDescriptor[]
    {
        new("PullRequests",  "Pull requests", "PullRequestIcon", true, RailAdornmentKind.None, typeof(PullRequestsViewModel)),
        new("Issues",        "Issues",        "IssueIcon",       true, RailAdornmentKind.None, typeof(IssuesViewModel)),
        new("Notifications", "Notifications", "BellIcon",        true, RailAdornmentKind.None, typeof(NotificationsViewModel)),
        new("Releases",      "Releases",      "TagIcon",         true, RailAdornmentKind.None, typeof(ReleasesViewModel)),
    };

    static EditionManifests()
    {
        // Wire the shell-owned host rail descriptors into the Pro-UI assembly's composition seam BEFORE the
        // Pro/Client singletons below are handed out and their Sections read (ProManifest.Sections composes
        // its three Pro destinations with these). Field initializers run first, but neither manifest reads
        // Sections at construction, so the seam is populated by the time anything enumerates it.
        ProComposition.HostRailSections = HostRailSections;
    }

    /// <summary>The shipped default: the full Pro agent platform.</summary>
    public static IEditionManifest Pro { get; } = new ProManifest();

    /// <summary>The plain Git client — no agent platform.</summary>
    public static IEditionManifest Client { get; } = new ClientManifest();
}
