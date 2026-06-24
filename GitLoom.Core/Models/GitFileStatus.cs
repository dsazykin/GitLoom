using LibGit2Sharp;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GitLoom.Core.Models;

public class GitFileStatus : INotifyPropertyChanged
{
    public string FilePath { get; set; } = string.Empty;

    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string DirectoryPath 
    {
        get
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath);
            return string.IsNullOrEmpty(dir) ? "" : dir;
        }
    }

    // The native LibGit2Sharp enum representing the exact state (e.g., ModifiedInWorkdir, AddedToIndex)
    public FileStatus State { get; set; }

    // Helper properties for the UI checkboxes
    // Staged = Anything marked "InIndex"
    public bool IsStaged => State.HasFlag(FileStatus.NewInIndex) ||
                            State.HasFlag(FileStatus.ModifiedInIndex) ||
                            State.HasFlag(FileStatus.DeletedFromIndex) ||
                            State.HasFlag(FileStatus.RenamedInIndex);

    // Unstaged = Anything marked "InWorkdir" or "Conflicted"
    public bool IsUnstaged => State.HasFlag(FileStatus.NewInWorkdir) ||
                              State.HasFlag(FileStatus.ModifiedInWorkdir) ||
                              State.HasFlag(FileStatus.DeletedFromWorkdir) ||
                              State.HasFlag(FileStatus.RenamedInWorkdir) ||
                              State.HasFlag(FileStatus.Conflicted);

    public bool IsUntracked => State.HasFlag(FileStatus.NewInWorkdir);
    public bool IsDeleted => State.HasFlag(FileStatus.DeletedFromWorkdir);
    public bool IsModified => State.HasFlag(FileStatus.ModifiedInWorkdir) || State.HasFlag(FileStatus.RenamedInWorkdir);

    // New property for checkbox selection
    private bool _isSelected = true; // Default to selected
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}