namespace GitLoom.Core.Models;

/// <summary>
/// Per-line blame attribution for the current version of a file (T-11). One row per
/// line of the blamed revision; blame data always flows to the UI through this type —
/// ViewModels never touch LibGit2Sharp <c>BlameHunk</c> objects.
/// </summary>
public sealed class BlameLine
{
    public int LineNumber { get; init; }            // 1-based, current file
    public string Sha { get; init; } = "";
    public string ShortSha { get; init; } = "";     // 8 chars
    public string AuthorName { get; init; } = "";
    public DateTimeOffset When { get; init; }
    public string Summary { get; init; } = "";      // commit MessageShort
}
