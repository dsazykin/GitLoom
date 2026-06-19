namespace GitLoom.Core.Models;

public class GitStashItem
{
    public int Index { get; set; }
    public string Message { get; set; } = string.Empty;
    public string FriendlyName => $"stash@{{{Index}}}: {Message}";
}
