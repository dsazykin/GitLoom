using System.Collections.Generic;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// The catalog of section-rail destinations a user can hide by unpinning (#78). Each id must match
/// a <c>RailSectionDescriptor.Id</c> in every edition manifest that offers it — <see
/// cref="Mainguard.App.Shell.ViewModels.MainWindowViewModel.RebuildRailSections"/> hides any rail
/// row whose id is listed here but missing from <c>UserPreferences.PinnedMenuIds</c>.
/// </summary>
public static class PinnableMenus
{
    public sealed record Definition(string Id, string Label, string IconResourceKey);

    public static readonly IReadOnlyList<Definition> All = new List<Definition>
    {
        new("PullRequests", "Pull Requests", "PullRequestIcon"),
        new("Issues", "Issues", "IssueIcon"),
        new("Notifications", "Notifications", "BellIcon"),
        new("Releases", "Releases", "TagIcon"),
    };
}
