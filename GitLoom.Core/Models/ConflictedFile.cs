namespace GitLoom.Core.Models;

public sealed class ConflictedFile
{
    public string Path { get; init; } = "";
    public bool HasBase   { get; init; }   // false on add/add conflicts
    public bool HasOurs   { get; init; }   // false when deleted on our side
    public bool HasTheirs { get; init; }   // false when deleted on their side
}
