namespace GitLoom.Core.Models;

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
    public string Message { get; set; } = string.Empty;
    public string? NewMessage { get; set; }
}
