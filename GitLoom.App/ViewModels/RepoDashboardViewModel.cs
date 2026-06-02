using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class RepoDashboardViewModel : ViewModelBase
{
    private readonly string _repoPath;
    private readonly IGitService _gitService;
    private readonly RepositoryWatcher _watcher;

    [ObservableProperty]
    private string _repositoryName;

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _stagedFiles = new();

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _unstagedFiles = new();
    
    [ObservableProperty]
    private GitFileStatus? _selectedFile;

    [ObservableProperty]
    private ObservableCollection<GitDiffLine> _diffLines = new();

    // The MVVM toolkit automatically calls this whenever SelectedFile changes
    partial void OnSelectedFileChanged(GitFileStatus? value)
    {
        if (value == null)
        {
            DiffLines.Clear();
            return;
        }

        var rawDiff = _gitService.GetFileDiff(_repoPath, value.FilePath, value.IsStaged);

        var parsedLines = new ObservableCollection<GitDiffLine>();
        if (!string.IsNullOrEmpty(rawDiff))
        {
            // Split the raw patch string by newlines
            foreach (var line in rawDiff.Split('\n'))
            {
                if (string.IsNullOrEmpty(line)) continue;

                parsedLines.Add(new GitDiffLine
                {
                    LineType = line[0], // The first char is always +, -, @, or a space
                    Content = line
                });
            }
        }

        DiffLines = parsedLines;
    }

    public RepoDashboardViewModel(Repository repository)
    {
        _repoPath = repository.Path;
        RepositoryName = repository.DisplayName;
        _gitService = new GitService();

        // Load immediately
        RefreshStatus();

        // Start listening for background folder changes
        _watcher = new RepositoryWatcher(_repoPath);
        _watcher.RepositoryChanged += OnRepositoryChanged;
    }

    private void OnRepositoryChanged()
    {
        // The watcher runs on a background thread. UI updates must be dispatched to the main UI thread.
        Dispatcher.UIThread.InvokeAsync(RefreshStatus);
    }

    private void RefreshStatus()
    {
        var allChanges = _gitService.GetRepositoryStatus(_repoPath);

        StagedFiles = new ObservableCollection<GitFileStatus>(allChanges.Where(f => f.IsStaged));
        UnstagedFiles = new ObservableCollection<GitFileStatus>(allChanges.Where(f => f.IsUnstaged));
    }
    
    [RelayCommand]
    private void StageFile(GitFileStatus file)
    {
        _gitService.StageFile(_repoPath, file.FilePath);

        // Note: We don't need to manually refresh the lists here!
        // Modifying the index will automatically trigger our RepositoryWatcher,
        // which instantly re-runs RefreshStatus() and updates the UI!
    }

    [RelayCommand]
    private void UnstageFile(GitFileStatus file)
    {
        _gitService.UnstageFile(_repoPath, file.FilePath);
    }
    
    [RelayCommand]
    private void StageSelectedFiles(IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var paths = selectedItems.Cast<GitFileStatus>().Select(f => f.FilePath).ToList();
        _gitService.StageFiles(_repoPath, paths);
    }

    [RelayCommand]
    private void UnstageSelectedFiles(IList? selectedItems)
    {
        if (selectedItems == null || selectedItems.Count == 0) return;

        var paths = selectedItems.Cast<GitFileStatus>().Select(f => f.FilePath).ToList();
        _gitService.UnstageFiles(_repoPath, paths);
    }
}