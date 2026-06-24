using System;
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
    private ObservableCollection<GitFileStatus> _versionedFiles = new();

    [ObservableProperty]
    private ObservableCollection<GitFileStatus> _unversionedFiles = new();

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
        
        // Remove old handlers
        foreach (var f in VersionedFiles) f.PropertyChanged -= File_PropertyChanged;
        foreach (var f in UnversionedFiles) f.PropertyChanged -= File_PropertyChanged;

        var versioned = allChanges.Where(f => !f.IsUntracked).ToList();
        var unversioned = allChanges.Where(f => f.IsUntracked).ToList();

        // Initialize IsSelected based on whether it was already staged, or true for untracked just for convenience
        foreach (var v in versioned) 
        {
            v.IsSelected = v.IsStaged;
            v.PropertyChanged += File_PropertyChanged;
        }
        foreach (var u in unversioned) 
        {
            u.IsSelected = false; // default untracked to not selected
            u.PropertyChanged += File_PropertyChanged;
        }

        VersionedFiles = new ObservableCollection<GitFileStatus>(versioned);
        UnversionedFiles = new ObservableCollection<GitFileStatus>(unversioned);

        UpdateTriStates();

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

    // --- Tri-State Checkboxes Logic ---
    
    [ObservableProperty]
    private bool? _isAllVersionedSelected = false;

    [ObservableProperty]
    private bool? _isAllUnversionedSelected = false;

    private bool _isUpdatingTriState = false;

    private void File_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GitFileStatus.IsSelected))
        {
            UpdateTriStates();
            CommitCommand.NotifyCanExecuteChanged();
            CommitAndPushCommand.NotifyCanExecuteChanged();
        }
    }

    private void UpdateTriStates()
    {
        if (_isUpdatingTriState) return;
        _isUpdatingTriState = true;

        if (VersionedFiles.Count == 0) IsAllVersionedSelected = false;
        else if (VersionedFiles.All(f => f.IsSelected)) IsAllVersionedSelected = true;
        else if (VersionedFiles.All(f => !f.IsSelected)) IsAllVersionedSelected = false;
        else IsAllVersionedSelected = null;

        if (UnversionedFiles.Count == 0) IsAllUnversionedSelected = false;
        else if (UnversionedFiles.All(f => f.IsSelected)) IsAllUnversionedSelected = true;
        else if (UnversionedFiles.All(f => !f.IsSelected)) IsAllUnversionedSelected = false;
        else IsAllUnversionedSelected = null;

        _isUpdatingTriState = false;
    }

    [RelayCommand]
    private void ToggleVersionedSelection()
    {
        bool targetState = (IsAllVersionedSelected == true) ? false : true;
        foreach (var f in VersionedFiles) f.IsSelected = targetState;
    }

    [RelayCommand]
    private void ToggleUnversionedSelection()
    {
        bool targetState = (IsAllUnversionedSelected == true) ? false : true;
        foreach (var f in UnversionedFiles) f.IsSelected = targetState;
    }

    // --- Commit Logic ---

    private bool CanCommit => !string.IsNullOrWhiteSpace(CommitMessage) && (VersionedFiles.Any(f => f.IsSelected) || UnversionedFiles.Any(f => f.IsSelected));

    private void PrepareStagingForCommit()
    {
        var selectedPaths = VersionedFiles.Where(f => f.IsSelected).Select(f => f.FilePath)
            .Concat(UnversionedFiles.Where(f => f.IsSelected).Select(f => f.FilePath)).ToList();
            
        var unselectedPaths = VersionedFiles.Where(f => !f.IsSelected).Select(f => f.FilePath)
            .Concat(UnversionedFiles.Where(f => !f.IsSelected).Select(f => f.FilePath)).ToList();

        // Stash/unstage is complex, but in our gitService, we can just unstage unselected, and stage selected.
        if (unselectedPaths.Count > 0)
        {
            _gitService.UnstageFiles(_repoPath, unselectedPaths);
        }
        if (selectedPaths.Count > 0)
        {
            _gitService.StageFiles(_repoPath, selectedPaths);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCommit))]
    private void Commit()
    {
        try
        {
            PrepareStagingForCommit();
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
            PrepareStagingForCommit();
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

    private bool CanStashPush => !string.IsNullOrWhiteSpace(StashMessage) && (VersionedFiles.Count > 0 || UnversionedFiles.Count > 0);

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
        catch (Exception ex)
        {
            Console.WriteLine($"Stash Pop Error: {ex.Message}");
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
        catch (Exception ex)
        {
            Console.WriteLine($"Stash Apply Error: {ex.Message}");
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
