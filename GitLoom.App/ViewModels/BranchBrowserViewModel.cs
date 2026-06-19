using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class BranchBrowserViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly Action _onBranchChangedAction;

    [ObservableProperty]
    private ObservableCollection<GitBranchItem> _localBranches = new();

    [ObservableProperty]
    private ObservableCollection<GitBranchItem> _remoteBranches = new();

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public BranchBrowserViewModel(IGitService gitService, string repoPath, Action onBranchChangedAction)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onBranchChangedAction = onBranchChangedAction;
    }

    public void LoadBranches()
    {
        var branches = _gitService.GetBranches(_repoPath).ToList();
        
        LocalBranches = new ObservableCollection<GitBranchItem>(branches.Where(b => !b.IsRemote).OrderBy(b => b.FriendlyName));
        RemoteBranches = new ObservableCollection<GitBranchItem>(branches.Where(b => b.IsRemote).OrderBy(b => b.FriendlyName));
        ErrorMessage = string.Empty;
    }

    [RelayCommand]
    private void CheckoutBranch(GitBranchItem branch)
    {
        try
        {
            if (_gitService.HasUncommittedChanges(_repoPath))
            {
                ErrorMessage = "Cannot checkout: You have uncommitted changes.";
                return;
            }

            _gitService.CheckoutBranch(_repoPath, branch.Name);
            ErrorMessage = string.Empty;
            _onBranchChangedAction?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Checkout failed: {ex.Message}";
        }
    }
}
