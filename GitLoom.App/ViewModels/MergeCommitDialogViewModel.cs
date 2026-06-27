using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public enum MergeCommitResult
{
    Cancel,
    Commit,
    CommitAndPush
}

public partial class MergeCommitDialogViewModel : ObservableObject
{
    private readonly string _repoPath;
    private readonly IGitService _gitService;
    private readonly Window _window;

    [ObservableProperty]
    private string _commitMessage;

    public MergeCommitResult Result { get; private set; } = MergeCommitResult.Cancel;

    public MergeCommitDialogViewModel(string repoPath, IGitService gitService, Window window)
    {
        _repoPath = repoPath;
        _gitService = gitService;
        _window = window;
        _commitMessage = _gitService.GetMergeMessage(_repoPath);
    }

    [RelayCommand]
    private void Commit()
    {
        try
        {
            _gitService.Commit(_repoPath, CommitMessage);
            Result = MergeCommitResult.Commit;
            _window.Close(true);
        }
        catch (Exception ex)
        {
            throw new Exception($"Commit failed: {ex.Message}", ex);
        }
    }

    [RelayCommand]
    private async Task CommitAndPush()
    {
        try
        {
            _gitService.Commit(_repoPath, CommitMessage);
            await Task.Run(() => _gitService.Push(_repoPath));
            Result = MergeCommitResult.CommitAndPush;
            _window.Close(true);
        }
        catch (Exception ex)
        {
            throw new Exception($"Commit/Push failed: {ex.Message}", ex);
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = MergeCommitResult.Cancel;
        _window.Close(false);
    }
}
