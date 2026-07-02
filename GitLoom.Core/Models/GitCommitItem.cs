using System;

namespace GitLoom.Core.Models;

public class GitCommitItem
{
    public string Sha { get; set; } = string.Empty;
    public string ShortSha => Sha.Length >= 7 ? Sha.Substring(0, 7) : Sha;
    public List<string> ParentShas { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public string MessageShort { get; set; } = string.Empty;
    public string AuthorName { get; set; } = string.Empty;
    public string AuthorEmail { get; set; } = string.Empty;
    public DateTimeOffset CommitDate { get; set; }
}
