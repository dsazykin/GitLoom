using System.Collections.Generic;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The catalog of top-nav menus a user can pin as their own icon button (#78). Scoped to the
/// items that already have a distinct icon resource in App.axaml — the issue's full wishlist
/// (Profiles, Worktrees, Analytics, Blame, Remotes, Accounts, SSH Keys, ...) can be added here
/// once each has its own unique glyph designed; reusing an existing icon for two pinned entries
/// would violate "each pinned icon must be visually unique".
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
        new("Submodules", "Submodules", "SubmoduleIcon"),
    };
}
