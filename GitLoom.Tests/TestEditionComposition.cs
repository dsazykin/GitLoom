using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GitLoom.App.Editions;
using GitLoom.App.ViewModels;

namespace GitLoom.Tests;

/// <summary>
/// Test-assembly stand-in for the Pro head's <c>WireProComposition</c> host-rail wiring (step 2f). The
/// removed <c>EditionManifests</c> static ctor used to populate <see cref="ProComposition.HostRailSections"/>
/// before any Pro manifest read its rail; in the split layout the Pro EXE head does that, which the tests
/// don't run. A module initializer re-establishes it once at assembly load — mirroring that static ctor —
/// so every test/harness that builds a <c>ProManifest</c> and reads its Sections (edition-shape tests,
/// completeness tests, the Pro render harnesses) sees the same host destinations the shipped Pro head wires.
/// The descriptors name the SHELL's host ViewModels, which this test assembly references directly.
/// </summary>
internal static class TestEditionComposition
{
    [ModuleInitializer]
    internal static void WireHostRail()
    {
        ProComposition.HostRailSections = new RailSectionDescriptor[]
        {
            new("PullRequests",  "Pull requests", "PullRequestIcon", true, RailAdornmentKind.None, typeof(PullRequestsViewModel)),
            new("Issues",        "Issues",        "IssueIcon",       true, RailAdornmentKind.None, typeof(IssuesViewModel)),
            new("Notifications", "Notifications", "BellIcon",        true, RailAdornmentKind.None, typeof(NotificationsViewModel)),
            new("Releases",      "Releases",      "TagIcon",         true, RailAdornmentKind.None, typeof(ReleasesViewModel)),
        };
    }
}
