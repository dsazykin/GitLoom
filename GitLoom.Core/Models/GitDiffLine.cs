namespace GitLoom.Core.Models;

public class GitDiffLine
{
    public string Content { get; set; } = string.Empty;
    public char LineType { get; set; }

    // Helpers for our Avalonia UI styles
    public bool IsAdded => LineType == '+';
    public bool IsRemoved => LineType == '-';
    public bool IsHeader => LineType == '@';
}