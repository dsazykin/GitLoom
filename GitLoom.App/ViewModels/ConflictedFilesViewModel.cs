using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

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
    private string _repoPath = null!;
    private IGitService _gitService = null!;
    private Avalonia.Controls.Window _window = null!;

    [ObservableProperty]
    private ObservableCollection<ConflictedFileItem> _conflictedFiles = new();

    [ObservableProperty]
    private bool _isRebasing;

    public string CancelButtonText => IsRebasing ? "Abort Rebase" : "Cancel Merge";

    public ConflictedFilesViewModel() { }

    public ConflictedFilesViewModel(string repoPath, IGitService gitService, Avalonia.Controls.Window window)
    {
        _repoPath = repoPath;
        _gitService = gitService;
        _window = window;
        IsRebasing = _gitService.IsRebasing(_repoPath);
        OnPropertyChanged(nameof(CancelButtonText));

        var statuses = _gitService.GetRepositoryStatus(_repoPath);
        foreach (var status in statuses.Where(s => s.State.HasFlag(LibGit2Sharp.FileStatus.Conflicted)))
        {
            ConflictedFiles.Add(new ConflictedFileItem(status.FilePath));
        }
    }

    private void RunGit(string args)
    {
        var psi = new ProcessStartInfo("git", args) { WorkingDirectory = _repoPath, CreateNoWindow = true };
        Process.Start(psi)?.WaitForExit();
    }

    [RelayCommand]
    private void AcceptIncoming(ConflictedFileItem item)
    {
        RunGit($"checkout --theirs \"{item.FilePath}\"");
        RunGit($"add \"{item.FilePath}\"");
        ConflictedFiles.Remove(item);
        CheckIfResolved();
    }

    [RelayCommand]
    private void AcceptOutgoing(ConflictedFileItem item)
    {
        RunGit($"checkout --ours \"{item.FilePath}\"");
        RunGit($"add \"{item.FilePath}\"");
        ConflictedFiles.Remove(item);
        CheckIfResolved();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task Modify(ConflictedFileItem item)
    {
        var dialog = new Views.ConflictResolverWindow();
        var vm = new ConflictResolverWindowViewModel(Path.Combine(_repoPath, item.FilePath), dialog);
        dialog.DataContext = vm;
        var result = await dialog.ShowDialog<bool>(_window);
        if (result)
        {
            RunGit($"add \"{item.FilePath}\"");
            ConflictedFiles.Remove(item);
            CheckIfResolved();
        }
    }

    private void CheckIfResolved()
    {
        if (ConflictedFiles.Count == 0)
        {
            _window?.Close();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        // During an interactive rebase there is no merge to abort — issuing
        // "merge --abort" is a no-op and leaves the rebase stuck. Abort the
        // operation that is actually in progress.
        RunGit(IsRebasing ? "rebase --abort" : "merge --abort");
        _window?.Close();
    }
}
