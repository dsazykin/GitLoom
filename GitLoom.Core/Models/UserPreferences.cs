namespace GitLoom.Core.Models;

public class UserPreferences
{
    // Theme key from the app's theme catalog (see GitLoom.App/Themes);
    // unknown/legacy values fall back to the default theme at startup.
    public string Theme { get; set; } = "MidnightLoom";
    public bool EnableGlassmorphism { get; set; } = true;
    public string AutoDetectPath { get; set; } = string.Empty;
    public string LastOpenedRepoPath { get; set; } = string.Empty;
    public System.Collections.Generic.Dictionary<string, bool> SidebarExpandedStates { get; set; } = new System.Collections.Generic.Dictionary<string, bool>();
    public double SidebarWidth { get; set; } = 280;

    // Timeline View Options
    public bool CompactReferencesView { get; set; } = true;
    public bool TagNames { get; set; } = false;
    public bool LongEdges { get; set; } = false;
    public bool CommitTimestamp { get; set; } = false;
    public bool ReferencesOnTheLeft { get; set; } = true;

    // Timeline Column Options
    public bool ShowAuthorColumn { get; set; } = true;
    public bool ShowDateColumn { get; set; } = true;
    public bool ShowHashColumn { get; set; } = true;

    // Timeline Highlight Options
    public bool HighlightMyCommits { get; set; } = true;
    public bool HighlightMergeCommits { get; set; } = false;
    public bool HighlightCurrentBranch { get; set; } = true;
    public bool HighlightNotCherryPickedCommits { get; set; } = false;
}
