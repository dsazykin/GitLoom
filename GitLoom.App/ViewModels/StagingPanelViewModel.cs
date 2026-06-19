using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class StagingPanelViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly Action _onCommitAction;

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _stagedFiles = new();

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _unstagedFiles = new();

    [ObservableProperty]
    private GitFileStatus? _selectedFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    private string _commitMessage = string.Empty;

    public event Action<GitFileStatus?>? SelectedFileChanged;

    public StagingPanelViewModel(IGitService gitService, string repoPath, Action onCommitAction)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onCommitAction = onCommitAction;
    }

    public void UpdateStatus(System.Collections.Generic.List<GitFileStatus> allChanges)
    {
        StagedFiles = new ObservableCollection<GitFileStatus>(allChanges.Where(f => f.IsStaged));
        UnstagedFiles = new ObservableCollection<GitFileStatus>(allChanges.Where(f => f.IsUnstaged).ToList());
        CommitCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFileChanged(GitFileStatus? value)
    {
        SelectedFileChanged?.Invoke(value);
    }

    partial void OnCommitMessageChanged(string value)
    {
        if (string.IsNullOrEmpty(value)) return;

        var replaced = value
            .Replace(":smile:", "😄")
            .Replace(":bug:", "🐛")
            .Replace(":sparkles:", "✨")
            .Replace(":memo:", "📝")
            .Replace(":rocket:", "🚀")
            .Replace(":tada:", "🎉")
            .Replace(":white_check_mark:", "✅")
            .Replace(":lipstick:", "💄")
            .Replace(":recycle:", "♻️")
            .Replace(":fire:", "🔥");

        if (replaced != value)
        {
            CommitMessage = replaced;
        }
    }

    private bool CanCommit => !string.IsNullOrWhiteSpace(CommitMessage) && StagedFiles.Count > 0;

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        _gitService.Commit(_repoPath, CommitMessage);
        CommitMessage = string.Empty;
        _onCommitAction?.Invoke();
    }

    [RelayCommand]
    private void StageFile(GitFileStatus file)
    {
        _gitService.StageFile(_repoPath, file.FilePath);
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

    [RelayCommand]
    private void StageAllFiles()
    {
        if (UnstagedFiles.Count == 0) return;
        var paths = UnstagedFiles.Select(f => f.FilePath).ToList();
        _gitService.StageFiles(_repoPath, paths);
    }

    [RelayCommand]
    private void UnstageAllFiles()
    {
        if (StagedFiles.Count == 0) return;
        var paths = StagedFiles.Select(f => f.FilePath).ToList();
        _gitService.UnstageFiles(_repoPath, paths);
    }
}
