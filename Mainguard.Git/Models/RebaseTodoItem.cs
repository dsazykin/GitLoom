namespace Mainguard.Git.Models;

public enum RebaseAction
{
    Pick,
    Reword,
    Squash,
    Fixup,
    Edit,
    Drop
}

public class RebaseTodoItem
{
    public string Sha { get; set; } = string.Empty;
    public RebaseAction Action { get; set; } = RebaseAction.Pick;

    /// <summary>Short (subject-line) message, used for the todo line and list display.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Full commit message (subject + body). Used as the reword default so bodies are not truncated.</summary>
    public string FullMessage { get; set; } = string.Empty;

    /// <summary>User-supplied replacement message for reword/squash. Null means "leave git's default".</summary>
    public string? NewMessage { get; set; }
}
