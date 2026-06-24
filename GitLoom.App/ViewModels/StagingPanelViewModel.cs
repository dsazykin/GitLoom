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
    private readonly Action<string, bool>? _showNotification;

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _stagedFiles = new();

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _unstagedFiles = new();

    [ObservableProperty]
    private ObservableCollection<GitStashItem> _stashes = new();

    [ObservableProperty]
    private bool _isRebasing;

    [ObservableProperty]
    private GitFileStatus? _selectedFile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StashPushCommand))]
    private string _stashMessage = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand))]
    [NotifyCanExecuteChangedFor(nameof(CommitAndPushCommand))]
    private string _commitMessage = string.Empty;

    public event Action<GitFileStatus?>? SelectedFileChanged;

    public StagingPanelViewModel(IGitService gitService, string repoPath, Action onCommitAction, Action<string, bool>? showNotification = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onCommitAction = onCommitAction;
        _showNotification = showNotification;
    }

    public void UpdateStatus(System.Collections.Generic.List<GitFileStatus> allChanges)
    {
        IsRebasing = _gitService.IsRebasing(_repoPath);
        StagedFiles = new ObservableCollection<GitFileStatus>(allChanges.Where(f => f.IsStaged));
        UnstagedFiles = new ObservableCollection<GitFileStatus>(allChanges.Where(f => f.IsUnstaged).ToList());
        CommitCommand.NotifyCanExecuteChanged();
        CommitAndPushCommand.NotifyCanExecuteChanged();
        StashPushCommand.NotifyCanExecuteChanged();
    }

    public void LoadStashes()
    {
        Stashes = new ObservableCollection<GitStashItem>(_gitService.GetStashes(_repoPath));
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
        try
        {
            _gitService.Commit(_repoPath, CommitMessage);
            CommitMessage = string.Empty;
            _onCommitAction?.Invoke();
        }
        catch (Exception)
        {
            // Ignored, handled by UI refresh
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void CommitAndPush()
    {
        try
        {
            _gitService.Commit(_repoPath, CommitMessage);
            CommitMessage = string.Empty;
            _onCommitAction?.Invoke();
            
            try
            {
                _gitService.Push(_repoPath);
                _showNotification?.Invoke("Commit and Push completed successfully.", false);
            }
            catch (Exception ex)
            {
                _showNotification?.Invoke($"Push Failed: {ex.Message}", true);
            }
        }
        catch (Exception ex)
        {
            _showNotification?.Invoke($"Commit Failed: {ex.Message}", true);
            // Ignored, handled by UI refresh
        }
    }

    [RelayCommand]
    private void ContinueRebase()
    {
        try
        {
            _gitService.ContinueRebase(_repoPath);
            _onCommitAction?.Invoke();
        }
        catch (Exception) { }
    }

    [RelayCommand]
    private void AbortRebase()
    {
        try
        {
            _gitService.AbortRebase(_repoPath);
            _onCommitAction?.Invoke();
        }
        catch (Exception) { }
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

    private bool CanStashPush => !string.IsNullOrWhiteSpace(StashMessage) && (StagedFiles.Count > 0 || UnstagedFiles.Count > 0);

    [RelayCommand(CanExecute = nameof(CanStashPush))]
    private void StashPush()
    {
        _gitService.StashPush(_repoPath, StashMessage);
        StashMessage = string.Empty;
        _onCommitAction?.Invoke(); // Trigger refresh
    }

    [RelayCommand]
    private void StashPop(GitStashItem stash)
    {
        try
        {
            _gitService.StashPop(_repoPath, stash.Index);
        }
        catch (System.Exception ex)
        {
            // Usually conflict exceptions, the UI watcher will refresh the dirty state anyway
            System.Console.WriteLine($"Stash Pop Error: {ex.Message}");
        }
        _onCommitAction?.Invoke();
    }

    [RelayCommand]
    private void StashApply(GitStashItem stash)
    {
        try
        {
            _gitService.StashApply(_repoPath, stash.Index);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Stash Apply Error: {ex.Message}");
        }
        _onCommitAction?.Invoke();
    }

    [RelayCommand]
    private void StashDrop(GitStashItem stash)
    {
        _gitService.StashDrop(_repoPath, stash.Index);
        _onCommitAction?.Invoke();
    }
}
