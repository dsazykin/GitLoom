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
        var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
        string currentBranchName = currentBranch?.FriendlyName ?? "current branch";
        
        var localViewModels = new ObservableCollection<MenuItemViewModel>();
        foreach (var b in branches.Where(x => !x.IsRemote).OrderBy(x => x.FriendlyName))
        {
            localViewModels.Add(CreateLocalBranchMenu(b, currentBranchName));
        }

        var remoteViewModels = new ObservableCollection<MenuItemViewModel>();
        foreach (var b in branches.Where(x => x.IsRemote).OrderBy(x => x.FriendlyName))
        {
            remoteViewModels.Add(CreateRemoteBranchMenu(b, currentBranchName));
        }

        LocalBranches = localViewModels;
        RemoteBranches = remoteViewModels;
        ErrorMessage = string.Empty;
    }

    private MenuItemViewModel CreateLocalBranchMenu(GitBranchItem branch, string currentBranchName)
    {
        var menu = new MenuItemViewModel { Header = branch.FriendlyName, IsCurrentBranch = branch.IsCurrentRepositoryHead };
        
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New branch from {branch.FriendlyName}", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Update", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Push", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Rename", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New worktree from {branch.FriendlyName}", Command = NotImplementedCommand });

        // Dummy tracked branch submenu
        var trackedMenu = new MenuItemViewModel { Header = $"Tracked branch (origin/{branch.FriendlyName})" };
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"New branch from origin/{branch.FriendlyName}", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"Checkout and rebase into {branch.FriendlyName}", Command = CheckoutAndRebaseCommand, CommandParameter = branch });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"Compare with {currentBranchName}", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"Rebase {branch.FriendlyName} into origin/{branch.FriendlyName}", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"Merge origin/{branch.FriendlyName} into {branch.FriendlyName}", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"New worktree from origin/{branch.FriendlyName}", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"Pull into {branch.FriendlyName} using rebase", Command = NotImplementedCommand });
        trackedMenu.SubItems.Add(new MenuItemViewModel { Header = $"Pull into {branch.FriendlyName} using merge", Command = NotImplementedCommand });
        
        menu.SubItems.Add(trackedMenu);
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });

        return menu;
    }

    private MenuItemViewModel CreateRemoteBranchMenu(GitBranchItem branch, string currentBranchName)
    {
        var menu = new MenuItemViewModel { Header = branch.FriendlyName };
        
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New branch from {branch.FriendlyName}", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Checkout and rebase into {currentBranchName}", Command = CheckoutAndRebaseCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Compare with {currentBranchName}", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Rebase {currentBranchName} into {branch.FriendlyName}", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Merge {branch.FriendlyName} into {currentBranchName}", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New worktree from {branch.FriendlyName}", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Pull into {currentBranchName} using rebase", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Pull into {currentBranchName} using merge", Command = NotImplementedCommand });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteBranchCommand, CommandParameter = branch });

        return menu;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CheckoutBranchAsync(GitBranchItem branch)
    {
        bool success = await PerformCheckoutWithFallbackAsync(branch.Name, branch.FriendlyName);
        if (success)
        {
            ErrorMessage = string.Empty;
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Checked out '{branch.FriendlyName}'");
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CheckoutAndRebaseAsync(GitBranchItem targetBranch)
    {
        try
        {
            var branches = _gitService.GetBranches(_repoPath).ToList();
            var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
            string currentBranchName = currentBranch?.FriendlyName ?? "main";

            bool success = await PerformCheckoutWithFallbackAsync(targetBranch.Name, targetBranch.FriendlyName);
            
            if (success)
            {
                _gitService.Rebase(_repoPath, currentBranchName);
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully checked out and rebased {targetBranch.FriendlyName} onto {currentBranchName}.");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Checkout and Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    private async System.Threading.Tasks.Task<bool> PerformCheckoutWithFallbackAsync(string branchName, string friendlyName)
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
                    return false;
                }
                else if (vm.Result == CheckoutConflictResult.Stash)
                {
                    _gitService.StashChanges(_repoPath, $"Auto-stash before checkout {friendlyName}");
                    _showNotificationAction?.Invoke("Changes stashed.");
                }
            }
        }

        try
        {
            _gitService.CheckoutBranch(_repoPath, branchName);
            return true;
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("conflict", StringComparison.OrdinalIgnoreCase))
            {
                if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var vm = new ConfirmationDialogViewModel
                    {
                        Title = "Checkout Conflicts",
                        Message = "Carry over failed due to conflicts. Would you like to stash your changes and continue checking out?",
                        ConfirmButtonText = "Stash & Checkout"
                    };
                    var dialog = new Views.ConfirmationDialog { DataContext = vm };
                    await dialog.ShowDialog(desktop.MainWindow);

                    if (vm.IsConfirmed)
                    {
                        _gitService.StashChanges(_repoPath, $"Auto-stash after failed carry over to {friendlyName}");
                        _gitService.CheckoutBranch(_repoPath, branchName);
                        _showNotificationAction?.Invoke("Changes stashed.");
                        return true;
                    }
                }
            }

            ErrorMessage = $"Checkout failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
            return false;
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
                    _gitService.CreateBranch(_repoPath, vm.BranchName, checkout: false);
                    ErrorMessage = string.Empty;
                    _onBranchChangedAction?.Invoke();
                    
                    if (vm.CheckoutImmediately)
                    {
                        bool checkedOut = await PerformCheckoutWithFallbackAsync(vm.BranchName, vm.BranchName);
                        if (checkedOut)
                        {
                            _onBranchChangedAction?.Invoke();
                            _showNotificationAction?.Invoke($"Created and checked out '{vm.BranchName}'");
                        }
                    }
                    else
                    {
                        _showNotificationAction?.Invoke($"Created branch '{vm.BranchName}'");
                    }
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
