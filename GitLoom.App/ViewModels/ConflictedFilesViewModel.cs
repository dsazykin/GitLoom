using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

public partial class ConflictedFileItem : ObservableObject
{
    [ObservableProperty]
    private string _filePath;

    public ConflictedFileItem(string filePath)
    {
        FilePath = filePath;
    }
}

public partial class ConflictedFilesViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ConflictedFileItem> _conflictedFiles = new();

    public ConflictedFilesViewModel()
    {
        // Mock data for UI testing - would be populated by Git service
        ConflictedFiles.Add(new ConflictedFileItem("src/main.js"));
        ConflictedFiles.Add(new ConflictedFileItem("README.md"));
    }

    [RelayCommand]
    private void AcceptIncoming(ConflictedFileItem item)
    {
        // TODO: Git logic to checkout --theirs
        ConflictedFiles.Remove(item);
    }

    [RelayCommand]
    private void AcceptOutgoing(ConflictedFileItem item)
    {
        // TODO: Git logic to checkout --ours
        ConflictedFiles.Remove(item);
    }

    [RelayCommand]
    private void Modify(ConflictedFileItem item)
    {
        // TODO: Open MergeConflictWindow for this file
    }

    [RelayCommand]
    private void Cancel()
    {
        // TODO: Git logic to abort merge
    }
}
