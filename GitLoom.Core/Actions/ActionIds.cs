namespace GitLoom.Core.Actions;

/// <summary>
/// Stable action identifiers shared by the <see cref="ActionRegistry"/>, the <see cref="ShortcutMap"/>
/// defaults, and the App-layer wiring (T-18). Keeping them as constants keeps the palette, the shortcut
/// bindings, and the persisted preferences referring to the same ids without stringly-typed drift.
/// </summary>
public static class ActionIds
{
    public const string OpenCommandPalette = "palette.open";
    public const string Commit = "commit";
    public const string Push = "push";
    public const string Pull = "pull";
    public const string Fetch = "fetch";
    public const string Refresh = "refresh";
    public const string NewBranch = "branch.new";
    public const string CloseRepository = "repo.close";
    public const string ToggleSidebar = "sidebar.toggle";
    public const string ManageRemotes = "remotes.manage";
    public const string ManageSubmodules = "submodules.manage";
    public const string ManageLfs = "lfs.manage";
    public const string ViewReflog = "reflog.view";
    public const string ViewPullRequests = "pullrequests.view";
    public const string OpenAnalytics = "analytics.open";
    public const string OpenCloudSync = "cloudsync.open";
}
