using LibGit2Sharp;

namespace GitLoom.Core.Models;

public class GitFileStatus
{
    public string FilePath { get; set; } = string.Empty;

    // The native LibGit2Sharp enum representing the exact state (e.g., ModifiedInWorkdir, AddedToIndex)
    public FileStatus State { get; set; }

    // Helper properties for the UI checkboxes
    // Staged = Anything marked "InIndex"
    public bool IsStaged => State.HasFlag(FileStatus.NewInIndex) ||
                            State.HasFlag(FileStatus.ModifiedInIndex) ||
                            State.HasFlag(FileStatus.DeletedFromIndex) ||
                            State.HasFlag(FileStatus.RenamedInIndex);

    // Unstaged = Anything marked "InWorkdir"
    public bool IsUnstaged => State.HasFlag(FileStatus.NewInWorkdir) ||
                              State.HasFlag(FileStatus.ModifiedInWorkdir) ||
                              State.HasFlag(FileStatus.DeletedFromWorkdir) ||
                              State.HasFlag(FileStatus.RenamedInWorkdir);
}