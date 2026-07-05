namespace GitLoom.Core.Models;

/// <summary>
/// A tag surfaced to the UI. Tag data always flows through this type — ViewModels
/// never touch LibGit2Sharp tag objects (T-05 invariant 3).
/// </summary>
public sealed class GitTagItem
{
    public string Name { get; init; } = "";
    public string TargetSha { get; init; } = "";   // peeled target COMMIT sha
    public bool IsAnnotated { get; init; }
    public string? Message { get; init; }           // annotated only
    public string? TaggerName { get; init; }        // annotated only
}
