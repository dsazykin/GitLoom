namespace GitLoom.Core.Models;

public class GitBranchItem
{
    public string Name { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public bool IsRemote { get; set; }
    public bool IsCurrentRepositoryHead { get; set; }
    public string TipSha { get; set; } = string.Empty;
}
