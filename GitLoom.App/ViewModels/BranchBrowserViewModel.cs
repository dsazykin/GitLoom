using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public class SeparatorViewModel : MenuItemViewModel {
    public SeparatorViewModel()
    {
        Header = "-";
        IsEnabled = false;
    }
}

public partial class BranchCategoryViewModel : ObservableObject
{
    [ObservableProperty]
    private string _categoryName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MenuItemViewModel> _branches = new();

    [ObservableProperty]
    private bool _isExpanded = true;
}

public partial class BranchBrowserViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly Action? _onBranchChangedAction;
    private readonly Action<string>? _showNotificationAction;
    private readonly Action<string>? _onCompareBranchAction;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private ObservableCollection<BranchCategoryViewModel> _branchCategories = new();

    [ObservableProperty]
    private string _currentBranchName = "Branches";

    public BranchBrowserViewModel(IGitService gitService, string repoPath, Action? onBranchChangedAction = null, Action<string>? showNotificationAction = null, Action<string>? onCompareBranchAction = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _onBranchChangedAction = onBranchChangedAction;
        _showNotificationAction = showNotificationAction;
        _onCompareBranchAction = onCompareBranchAction;
    }

    public void LoadBranches()
    {
        var branches = _gitService.GetBranches(_repoPath).ToList();
        var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
        string currentBranchName = currentBranch?.FriendlyName ?? "Branches";
        CurrentBranchName = currentBranchName;
        
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

        var oldCategories = BranchCategories.ToDictionary(c => c.CategoryName, c => c.IsExpanded);

        var newCategories = new ObservableCollection<BranchCategoryViewModel>
        {
            new BranchCategoryViewModel { CategoryName = "Recent", Branches = new ObservableCollection<MenuItemViewModel>(localViewModels.Take(3)) },
            new BranchCategoryViewModel { CategoryName = "Local", Branches = localViewModels },
            new BranchCategoryViewModel { CategoryName = "Remote", Branches = remoteViewModels }
        };

        foreach (var category in newCategories)
        {
            if (oldCategories.TryGetValue(category.CategoryName, out var wasExpanded))
            {
                category.IsExpanded = wasExpanded;
            }
        }

        BranchCategories = newCategories;
        
        ErrorMessage = string.Empty;
    }

    private MenuItemViewModel CreateLocalBranchMenu(GitBranchItem branch, string currentBranchName)
    {
        var menu = new MenuItemViewModel { Header = branch.FriendlyName, IsCurrentBranch = branch.IsCurrentRepositoryHead };
        
        // Group 1: Workspace Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New branch from {branch.FriendlyName}", Command = CreateBranchFromCommand, CommandParameter = branch });
        
        menu.SubItems.Add(new SeparatorViewModel());

        // Group 2: Remote Operations
        menu.SubItems.Add(new MenuItemViewModel { Header = "Push", Command = PushBranchCommand, CommandParameter = branch });
        
        menu.SubItems.Add(new SeparatorViewModel());

        // Group 3: Integration
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Merge {branch.FriendlyName} into {currentBranchName}", Command = MergeIntoCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Rebase {currentBranchName} onto {branch.FriendlyName}", Command = RebaseIntoCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });
        
        menu.SubItems.Add(new SeparatorViewModel());

        // Group 4: Review
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = ShowDiffWithWorkingTreeCommand, CommandParameter = branch });
        
        menu.SubItems.Add(new SeparatorViewModel());

        // Group 5: Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Rename", Command = RenameBranchCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = "Delete", Command = DeleteBranchCommand, CommandParameter = branch, IsEnabled = !branch.IsCurrentRepositoryHead });

        return menu;
    }

    private MenuItemViewModel CreateRemoteBranchMenu(GitBranchItem branch, string currentBranchName)
    {
        var menu = new MenuItemViewModel
        {
            Header = branch.FriendlyName,
            IsCurrentBranch = branch.IsCurrentRepositoryHead,
            SubItems = new ObservableCollection<MenuItemViewModel>()
        };

        // Group 1: Workspace Management
        menu.SubItems.Add(new MenuItemViewModel { Header = "Checkout", Command = CheckoutBranchCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"New branch from {branch.FriendlyName}", Command = CreateBranchFromCommand, CommandParameter = branch });
        
        menu.SubItems.Add(new SeparatorViewModel());

        // Group 2: Integration
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Merge {branch.FriendlyName} into {currentBranchName}", Command = MergeIntoCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Rebase {currentBranchName} onto {branch.FriendlyName}", Command = RebaseIntoCommand, CommandParameter = branch });
        
        menu.SubItems.Add(new SeparatorViewModel());

        // Group 3: Review
        menu.SubItems.Add(new MenuItemViewModel { Header = "Show diff with working tree", Command = ShowDiffWithWorkingTreeCommand, CommandParameter = branch });
        menu.SubItems.Add(new MenuItemViewModel { Header = $"Compare with {currentBranchName}", Command = CompareBranchesCommand, CommandParameter = branch });
        
        menu.SubItems.Add(new SeparatorViewModel());

        // Group 4: Management
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

    [RelayCommand]
    private void RebaseInto(GitBranchItem targetBranch)
    {
        try
        {
            var branches = _gitService.GetBranches(_repoPath).ToList();
            var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
            string currentBranchName = currentBranch?.FriendlyName ?? "main";

            _gitService.Rebase(_repoPath, targetBranch.FriendlyName);
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully rebased {currentBranchName} onto {targetBranch.FriendlyName}.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RebaseLocalIntoTrackedAsync(GitBranchItem localBranch)
    {
        try
        {
            bool success = await PerformCheckoutWithFallbackAsync(localBranch.Name, localBranch.FriendlyName);
            if (success)
            {
                _gitService.Rebase(_repoPath, $"origin/{localBranch.FriendlyName}");
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully rebased {localBranch.FriendlyName} onto origin/{localBranch.FriendlyName}.");
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task PullWithRebaseAsync(GitBranchItem branch)
    {
        try
        {
            _gitService.Fetch(_repoPath);
            
            if (branch.IsRemote)
            {
                var branches = _gitService.GetBranches(_repoPath).ToList();
                var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
                string currentBranchName = currentBranch?.FriendlyName ?? "main";

                _gitService.Rebase(_repoPath, branch.FriendlyName);
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully pulled and rebased {branch.FriendlyName} into {currentBranchName}.");
            }
            else
            {
                bool success = await PerformCheckoutWithFallbackAsync(branch.Name, branch.FriendlyName);
                if (success)
                {
                    _gitService.Rebase(_repoPath, $"origin/{branch.FriendlyName}");
                    _onBranchChangedAction?.Invoke();
                    _showNotificationAction?.Invoke($"Successfully pulled and rebased origin/{branch.FriendlyName} into {branch.FriendlyName}.");
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Pull with Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task MergeIntoAsync(GitBranchItem branch)
    {
        try
        {
            var branches = _gitService.GetBranches(_repoPath).ToList();
            var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
            if (currentBranch == null) return;
            
            string sourceBranchName = branch.IsRemote ? branch.FriendlyName : branch.Name;
            
            if (branch.IsRemote)
            {
                _gitService.Fetch(_repoPath);
            }

            _gitService.Merge(_repoPath, sourceBranchName);
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully merged {sourceBranchName} into {currentBranch.FriendlyName}.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Merge failed: {ex.Message}";
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
        ErrorMessage = "Action coming soon!";
        _showNotificationAction?.Invoke(ErrorMessage);
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RebaseCurrentOntoAsync(GitBranchItem branch)
    {
        try
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var vm = new ConfirmationDialogViewModel
                {
                    Title = "Rebase",
                    Message = $"Are you sure you want to rebase your current branch onto '{branch.FriendlyName}'?",
                    ConfirmButtonText = "Rebase"
                };
                var dialog = new Views.ConfirmationDialog { DataContext = vm };
                await dialog.ShowDialog(desktop.MainWindow);

                if (!vm.IsConfirmed) return;
            }

            _gitService.Rebase(_repoPath, branch.FriendlyName);
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully rebased current branch onto '{branch.FriendlyName}'.");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Rebase failed: {ex.Message}";
            _showNotificationAction?.Invoke(ErrorMessage);
        }
    }

    [RelayCommand]
    private void ShowDiff(GitBranchItem branch)
    {
        try
        {
            // For now, we generate a high-level patch or notify. The backend takes a specific file, but we can pass null for the whole tree diffing if supported by LibGit2Sharp, or just simulate the UI action.
            // Since GitService.GetDiffAgainstCommit takes a file path, we might just want to show the count of worktrees as a test for Phase 4.5.
            _showNotificationAction?.Invoke($"Diff generation backend ready for {branch.FriendlyName}. Connect DiffViewer UI next.");
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Diff failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task NewWorktreeAsync(GitBranchItem branch)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var dialog = new Avalonia.Controls.OpenFolderDialog
            {
                Title = $"Select folder for new worktree ({branch.FriendlyName})"
            };
            var result = await dialog.ShowAsync(desktop.MainWindow);
            if (!string.IsNullOrEmpty(result))
            {
                try
                {
                    _gitService.AddWorktree(_repoPath, result, branch.Name);
                    _showNotificationAction?.Invoke($"Successfully created new worktree at {result}.");
                }
                catch (Exception ex)
                {
                    _showNotificationAction?.Invoke($"Failed to create worktree: {ex.Message}");
                }
            }
        }
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
                    _gitService.CreateBranch(_repoPath, vm.BranchName, "", checkout: false);
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

    [RelayCommand]
    private async System.Threading.Tasks.Task CreateBranchFromAsync(GitBranchItem branch)
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
                    _gitService.CreateBranch(_repoPath, vm.BranchName, branch.Name, checkout: false);
                    _onBranchChangedAction?.Invoke();
                    
                    if (vm.CheckoutImmediately)
                    {
                        bool checkedOut = await PerformCheckoutWithFallbackAsync(vm.BranchName, vm.BranchName);
                        if (checkedOut)
                        {
                            _onBranchChangedAction?.Invoke();
                            _showNotificationAction?.Invoke($"Created and checked out '{vm.BranchName}' from '{branch.FriendlyName}'.");
                        }
                    }
                    else
                    {
                        _showNotificationAction?.Invoke($"Created branch '{vm.BranchName}' from '{branch.FriendlyName}'.");
                    }
                }
                catch (Exception ex)
                {
                    _showNotificationAction?.Invoke($"Create Branch Failed: {ex.Message}");
                }
            }
        }
    }

    [RelayCommand]
    private void UpdateBranch(GitBranchItem branch)
    {
        try
        {
            if (branch.IsCurrentRepositoryHead)
            {
                _gitService.Pull(_repoPath);
            }
            else
            {
                _gitService.Fetch(_repoPath);
                _gitService.Merge(_repoPath, $"origin/{branch.FriendlyName}");
            }
            _onBranchChangedAction?.Invoke();
            _showNotificationAction?.Invoke($"Successfully updated '{branch.FriendlyName}'.");
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Update Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void PushBranch(GitBranchItem branch)
    {
        try
        {
            _gitService.PushBranch(_repoPath, branch.Name);
            _showNotificationAction?.Invoke($"Successfully pushed '{branch.FriendlyName}'.");
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Push Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task RenameBranchAsync(GitBranchItem branch)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new CreateBranchDialogViewModel();
            // Using CreateBranch dialog as a quick hack for rename since it has a text input
            var dialog = new Views.CreateBranchDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            
            if (vm.IsConfirmed)
            {
                try
                {
                    _gitService.RenameBranch(_repoPath, branch.Name, vm.BranchName);
                    _onBranchChangedAction?.Invoke();
                    _showNotificationAction?.Invoke($"Renamed '{branch.FriendlyName}' to '{vm.BranchName}'.");
                }
                catch (Exception ex)
                {
                    _showNotificationAction?.Invoke($"Rename Failed: {ex.Message}");
                }
            }
        }
    }

    [RelayCommand]
    private void ShowDiffWithWorkingTree(GitBranchItem branch)
    {
        try
        {
            var diff = _gitService.GetBranchDiffAgainstWorkingTree(_repoPath, branch.Name);
            if (string.IsNullOrWhiteSpace(diff))
            {
                _showNotificationAction?.Invoke($"No differences between working tree and {branch.FriendlyName}.");
                return;
            }
            
            string tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GitLoom_{branch.FriendlyName.Replace("/", "_")}_diff.patch");
            System.IO.File.WriteAllText(tempFile, diff);
            
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Failed to generate diff: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CompareBranches(GitBranchItem branch)
    {
        _onCompareBranchAction?.Invoke(branch.Name);
        _showNotificationAction?.Invoke($"Commit timeline filtered by {branch.FriendlyName}");
    }


    [RelayCommand]
    private void PullWithMerge(GitBranchItem branch)
    {
        try
        {
            _gitService.Fetch(_repoPath);
            
            if (branch.IsRemote)
            {
                var branches = _gitService.GetBranches(_repoPath).ToList();
                var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
                string currentBranchName = currentBranch?.FriendlyName ?? "main";

                _gitService.Merge(_repoPath, branch.FriendlyName);
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully pulled and merged {branch.FriendlyName} into {currentBranchName}.");
            }
            else
            {
                _gitService.Merge(_repoPath, $"origin/{branch.FriendlyName}");
                _onBranchChangedAction?.Invoke();
                _showNotificationAction?.Invoke($"Successfully pulled and merged origin/{branch.FriendlyName} into {branch.FriendlyName}.");
            }
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke($"Pull with Merge failed: {ex.Message}");
        }
    }
}
