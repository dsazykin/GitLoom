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
    private readonly Action<string> _showNotificationAction;

    [ObservableProperty]
    private ObservableCollection<MenuItemViewModel> _localBranches = new();

    [ObservableProperty]
    private ObservableCollection<MenuItemViewModel> _remoteBranches = new();

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public BranchBrowserViewModel(IGitService gitService, string repoPath, Action onBranchChangedAction, Action<string> showNotificationAction)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onBranchChangedAction = onBranchChangedAction;
        _showNotificationAction = showNotificationAction;
    }

    public void LoadBranches()
    {
        var branches = _gitService.GetBranches(_repoPath).ToList();
        
        var localViewModels = new ObservableCollection<MenuItemViewModel>();
        foreach (var b in branches.Where(x => !x.IsRemote).OrderBy(x => x.FriendlyName))
        {
            localViewModels.Add(CreateLocalBranchMenu(b));
        }

        var remoteViewModels = new ObservableCollection<MenuItemViewModel>();
        foreach (var b in branches.Where(x => x.IsRemote).OrderBy(x => x.FriendlyName))
        {
            remoteViewModels.Add(CreateRemoteBranchMenu(b));
        }

        LocalBranches = localViewModels;
        RemoteBranches = remoteViewModels;
        ErrorMessage = string.Empty;
    }

    private MenuItemViewModel CreateLocalBranchMenu(GitBranchItem branch)
    {
        var menu = new MenuItemViewModel { Header = branch.FriendlyName, IsCurrentBranch = branch.IsCurrentRepositoryHead };
        
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });
        menu.SubItems.Add(new MenuItemViewModel { Header = "New branch from this branch", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Update", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Push", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Rename", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "New worktree from main", Command = NotImplementedCommand });

        // Dummy tracked branch submenu
        var trackedMenu = new MenuItemViewModel { Header = $"Tracked branch (origin/{branch.FriendlyName})" };
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "New branch from (tracked branch)", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Checkout and rebase into (branch)", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Compare with (current branch)", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Rebase (local) into (remote)", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Merge (remote) into (local)", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "New worktree from (name)", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Pull into (name) using rebase", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Pull into (name) using merge", Command = NotImplementedCommand });
        
        menu.SubItems.Add(trackedMenu);
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });

        return menu;
    }

    private MenuItemViewModel CreateRemoteBranchMenu(GitBranchItem branch)
    {
        var menu = new MenuItemViewModel { Header = branch.FriendlyName };
        
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = "New branch from (name)", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout and rebase into (current)", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Compare with (current)", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Rebase (current) into (remote)", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Merge (remote) into (current)", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "New worktree from (name)", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Pull into (current) using rebase", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Pull into (current) using merge", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteBranchCommand, CommandParameter = branch });

        return menu;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CheckoutBranchAsync(GitBranchItem branch)
    {
        try
        {
            if (_gitService.HasUncommittedChanges(_repoPath))
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var vm = new CheckoutConflictDialogViewModel();
                    var dialog = new Views.CheckoutConflictDialog { DataContext = vm };
                    await dialog.ShowDialog(desktop.MainWindow);

                    if (vm.Result == CheckoutConflictResult.Cancel)
                    {
                        return;
                    }
                    else if (vm.Result == CheckoutConflictResult.Stash)
                    {
                        // Needs Stash API in IGitService. We can implement a quick fallback or call native CLI
                        _gitService.StashChanges(_repoPath, "Auto-stash before checkout");
                        _showNotificationAction?.Invoke("Changes stashed.");
                    }
                    // If CarryOver, we just do nothing and let LibGit2Sharp try to checkout (it will carry over non-conflicting changes)
                }
            }

            _gitService.CheckoutBranch(_repoPath, branch.Name);
            ErrorMessage = string.Empty;
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Checked out '{branch.FriendlyName}'");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Checkout failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteBranchAsync(GitBranchItem branch)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var vm = new ConfirmationDialogViewModel
                {
                    Title = "Delete Branch",
                    Message = $"Are you sure you want to delete the branch '{branch.FriendlyName}'?\nThis action cannot be undone.",
                    ConfirmButtonText = "Delete"
                };
                var dialog = new Views.ConfirmationDialog { DataContext = vm };
                await dialog.ShowDialog(desktop.MainWindow);

                if (!vm.IsConfirmed)
                {
                    return;
                }
            }

            _gitService.DeleteBranch(_repoPath, branch.Name);
            ErrorMessage = string.Empty;
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Deleted branch '{branch.FriendlyName}'");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Delete failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private void NotImplemented()
    {
        ErrorMessage = "Action coming soon (Phase 4.5)!";
        _showNotificationAction?.Invoke(ErrorMessage);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task OpenCreateBranchDialogAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new CreateBranchDialogViewModel();
            var dialog = new Views.CreateBranchDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            
            if (vm.IsConfirmed)
            {
                try
                {
                    _gitService.CreateBranch(_repoPath, vm.BranchName, vm.CheckoutImmediately);
                    ErrorMessage = string.Empty;
                    _onBranchChangedAction?.Invoke();
                    
                    if (vm.CheckoutImmediately)
                        _showNotificationAction?.Invoke($"Created and checked out '{vm.BranchName}'");
                    else
                        _showNotificationAction?.Invoke($"Created branch '{vm.BranchName}'");
                }
                catch (Exception ex)
                {
                    ErrorMessage = $"Create branch failed: {ex.Message}";
                    _showNotificationAction?.Invoke(ErrorMessage);
                }
            }
        }
    }
}
