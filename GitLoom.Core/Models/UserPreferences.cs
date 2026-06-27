namespace GitLoom.Core.Models;

public class UserPreferences
{
    public string Theme { get; set; } = "Dark";
    public bool EnableGlassmorphism { get; set; } = true;
    public string AutoDetectPath { get; set; } = string.Empty;
    public System.Collections.Generic.Dictionary<string, bool> SidebarExpandedStates { get; set; } = new System.Collections.Generic.Dictionary<string, bool>();

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
