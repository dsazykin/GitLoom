namespace Mainguard.Git.Exceptions;

/// <summary>
/// Raised when an operation-journal Undo/Redo is refused (T-19): the working tree
/// has uncommitted changes that a worktree reset would clobber, the entry is flagged
/// non-undoable (e.g. a push), or the redo stack was truncated by a newer operation.
/// The refusal mutates nothing — the repository is left exactly as it was.
/// </summary>
public class UndoBlockedException : MainguardException
{
    public UndoBlockedException(string message) : base(message) { }
    public UndoBlockedException(string message, System.Exception inner) : base(message, inner) { }
}
