using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Views;
using GitLoom.Core;
using GitLoom.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace GitLoom.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<WorkspaceCategory> _categories =
        new();

    [ObservableProperty]
    private ViewModelBase? _currentWorkspace;

    // Dispose a workspace when it is replaced/cleared so its background resources
    // (RepositoryWatcher, AutoFetchService loop — T-10) don't leak across repos.
    partial void OnCurrentWorkspaceChanging(ViewModelBase? oldValue, ViewModelBase? newValue)
    {
        if (!ReferenceEquals(oldValue, newValue) && oldValue is System.IDisposable disposable)
            disposable.Dispose();
    }

    [ObservableProperty]
    private object? _selectedNode;

    [ObservableProperty]
    private bool _isDeleteConfirmationVisible;

    [ObservableProperty]
    private string _deleteConfirmationTitle = "Confirm Delete";

    [ObservableProperty]
    private string _deleteConfirmationMessage = string.Empty;

    private object? _nodeToDelete;

    /// <summary>Switch the app theme (File menu → Theme). Applies live and persists the choice.</summary>
    [RelayCommand]
    private void SetTheme(string themeKey) => Theming.ThemeManager.Apply(themeKey);

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        IsDeleteConfirmationVisible = false;
        if (_nodeToDelete is WorkspaceCategory cat)
        {
            ExecuteDeleteCategory(cat);
        }
        else if (_nodeToDelete is Repository repo)
        {
            await ExecuteRemoveRepositoryAsync(repo);
        }
        _nodeToDelete = null;
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsDeleteConfirmationVisible = false;
        _nodeToDelete = null;
    }

    [ObservableProperty]
    private bool _isSidebarOpen = true;

    [ObservableProperty]
    private bool _isInvalidRepoCardVisible;

    [ObservableProperty]
    private Repository? _invalidRepository;

    [ObservableProperty]
    private bool _isReopenRepoCardVisible;

    [ObservableProperty]
    private string _reopenRepositoryPath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAutoDetectPath))]
    private string _autoDetectPath = string.Empty;

    public bool HasAutoDetectPath => !string.IsNullOrEmpty(AutoDetectPath);

    // Shared with the rest of the app (GitLoom.App.App.Settings) — a private instance here would cache
    // its own UserPreferences snapshot and clobber concurrent writes from other owners (#83).
    private readonly GitLoom.Core.Services.ISettingsService _settingsService = GitLoom.App.App.Settings;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
        if (IsSidebarOpen)
        {
            SidebarColumnWidth = new Avalonia.Controls.GridLength(_settingsService.Current.SidebarWidth, Avalonia.Controls.GridUnitType.Pixel);
            SidebarColumnMinWidth = 200;
        }
        else
        {
            SidebarColumnWidth = new Avalonia.Controls.GridLength(0, Avalonia.Controls.GridUnitType.Pixel);
            SidebarColumnMinWidth = 0;
        }
    }

    [ObservableProperty]
    private Avalonia.Controls.GridLength _sidebarColumnWidth;

    [ObservableProperty]
    private double _sidebarColumnMinWidth = 200;

    partial void OnSidebarColumnWidthChanged(Avalonia.Controls.GridLength value)
    {
        if (value.IsAbsolute && value.Value > 0)
        {
            _settingsService.Update(p => p.SidebarWidth = value.Value);
        }
    }

    // --- Command palette & keyboard shortcuts (T-18) ---

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    // The UI-free action catalog the palette and the global shortcuts both invoke. Built once; each
    // action's CanExecute/Execute closes over live state, so availability tracks the open repo.
    private readonly GitLoom.Core.Actions.ActionRegistry _actionRegistry = new();

    /// <summary>The palette overlay's ViewModel (fuzzy filtering + ranked rows live here).</summary>
    public CommandPaletteViewModel CommandPalette { get; }

    /// <summary>The effective shortcut bindings = built-in defaults overlaid with the user's saved overrides.</summary>
    public GitLoom.Core.Actions.ShortcutMap Shortcuts =>
        GitLoom.Core.Actions.ShortcutMap.FromPreferences(_settingsService.Current.ShortcutBindings);

    /// <summary>Raised when the palette opens, so the view can focus the query box.</summary>
    public event System.Action? CommandPaletteOpened;

    /// <summary>Raised after the user saves rebinds, so the window can rebuild its KeyBindings.</summary>
    public event System.Action? ShortcutsChanged;

    /// <summary>Opens the keyboard-shortcut rebind window; persists overrides and rebuilds bindings on save.</summary>
    [RelayCommand]
    private async Task OpenShortcutSettingsAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
            || desktop.MainWindow is null)
            return;

        var actions = _actionRegistry.All.Select(a => (a.Id, a.Title)).ToList();
        var vm = new ShortcutSettingsViewModel(Shortcuts, actions, overrides =>
        {
            _settingsService.Update(p => p.ShortcutBindings = overrides);
            ShortcutsChanged?.Invoke();
        });
        var window = new ShortcutSettingsWindow { DataContext = vm };
        await window.ShowDialog(desktop.MainWindow);
    }

    private RepoDashboardViewModel? Dashboard => CurrentWorkspace as RepoDashboardViewModel;

    [RelayCommand]
    private void OpenCommandPalette()
    {
        CommandPalette.Reset();
        IsCommandPaletteOpen = true;
        CommandPaletteOpened?.Invoke();
    }

    [RelayCommand]
    private void CloseCommandPalette() => IsCommandPaletteOpen = false;

    /// <summary>Routes a global keyboard gesture to its action (built from the ShortcutMap by the window).
    /// Silently ignores unknown or currently-unavailable actions.</summary>
    [RelayCommand]
    private void InvokeActionById(string? actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return;
        var action = _actionRegistry.Find(actionId);
        if (action is null) return;
        bool available;
        try { available = action.CanExecute(); } catch { available = false; }
        if (!available) return;
        _ = action.Execute();
    }

    // Builds the palette candidate set fresh on each open: enabled actions, then the open repo's local
    // branches, then bookmarked repositories.
    private System.Collections.Generic.IReadOnlyList<PaletteEntry> BuildPaletteEntries()
    {
        var entries = new System.Collections.Generic.List<PaletteEntry>();
        var shortcuts = Shortcuts;

        foreach (var action in _actionRegistry.Enabled())
        {
            entries.Add(new PaletteEntry(
                action.Title,
                action.Category,
                shortcuts.GestureFor(action.Id) ?? string.Empty,
                action.Execute));
        }

        if (Dashboard is { } dash)
        {
            foreach (var branch in dash.ListLocalBranches().Where(b => !b.IsCurrentRepositoryHead))
            {
                var b = branch;
                entries.Add(new PaletteEntry(
                    $"Checkout {b.FriendlyName}", "Branch", string.Empty,
                    () => { dash.CheckoutBranchFromPalette(b); return System.Threading.Tasks.Task.CompletedTask; }));
            }
        }

        foreach (var repo in Categories.SelectMany(c => c.Repositories))
        {
            var r = repo;
            entries.Add(new PaletteEntry(
                $"Open {r.DisplayName}", "Repository", string.Empty,
                () => { OpenRepository(r); return System.Threading.Tasks.Task.CompletedTask; }));
        }

        return entries;
    }

    private void RegisterActions()
    {
        var ids = typeof(GitLoom.Core.Actions.ActionIds);
        void Add(string id, string title, string category, System.Func<bool> can, System.Action run) =>
            _actionRegistry.Register(new GitLoom.Core.Actions.AppAction
            {
                Id = id,
                Title = title,
                Category = category,
                CanExecute = can,
                Execute = () => { run(); return System.Threading.Tasks.Task.CompletedTask; },
            });

        Add(GitLoom.Core.Actions.ActionIds.OpenCommandPalette, "Open Command Palette", "General",
            () => true, OpenCommandPalette);
        Add(GitLoom.Core.Actions.ActionIds.Commit, "Commit", "Repository",
            () => Dashboard?.StagingPanel.CommitCommand.CanExecute(null) ?? false,
            () => Dashboard?.StagingPanel.CommitCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.Push, "Push", "Repository",
            () => Dashboard?.PushCommand.CanExecute(null) ?? false,
            () => Dashboard?.PushCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.Pull, "Pull", "Repository",
            () => Dashboard?.PullCommand.CanExecute(null) ?? false,
            () => Dashboard?.PullCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.Fetch, "Fetch", "Repository",
            () => Dashboard?.FetchCommand.CanExecute(null) ?? false,
            () => Dashboard?.FetchCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.Refresh, "Refresh Status", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.StagingPanel.RefreshCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.NewBranch, "New Branch…", "Branch",
            () => Dashboard is not null,
            () => Dashboard?.BranchBrowser.OpenCreateBranchDialogCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.CloseRepository, "Close Repository", "General",
            () => CurrentWorkspace is not null, CloseRepository);
        Add(GitLoom.Core.Actions.ActionIds.ToggleSidebar, "Toggle Sidebar", "View",
            () => true, ToggleSidebar);
        Add(GitLoom.Core.Actions.ActionIds.ManageRemotes, "Manage Remotes…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageRemotesCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.ManageSubmodules, "Manage Submodules…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageSubmodulesCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.ManageLfs, "Manage Git LFS…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageLfsCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.ViewReflog, "View Reflog…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ViewReflogCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.ViewPullRequests, "Pull Requests…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManagePullRequestsCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.ViewIssues, "Issues…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageIssuesCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.ViewNotifications, "Notifications…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageNotificationsCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.ViewReleases, "Releases…", "Repository",
            () => Dashboard is not null,
            () => Dashboard?.ManageReleasesCommand.Execute(null));
        Add(GitLoom.Core.Actions.ActionIds.OpenAnalytics, "Open Analytics", "View",
            () => Dashboard is not null,
            () => { if (Dashboard is { } d) OpenAnalytics(new Repository { Path = d.RepositoryPath, DisplayName = d.RepositoryName }); });
        Add(GitLoom.Core.Actions.ActionIds.OpenCloudSync, "Clone / Cloud Sync", "General",
            () => true, OpenCloudSync);
    }

    // This is automatically triggered by the MVVM Toolkit whenever _selectedNode changes!
    partial void OnSelectedNodeChanged(object? value)
    {
        ClearAllSelections(Categories);

        if (value is Repository repo)
        {
            repo.IsSelected = true;
        }
        else if (value is WorkspaceCategory cat)
        {
            cat.IsSelected = true;
        }
    }

    private void ClearAllSelections(IEnumerable<WorkspaceCategory> categories)
    {
        foreach (var cat in categories)
        {
            cat.IsSelected = false;
            foreach (var repo in cat.Repositories)
            {
                repo.IsSelected = false;
            }
            if (cat.SubCategories != null)
            {
                ClearAllSelections(cat.SubCategories);
            }
        }
    }

    // True while a repo is being opened — lets the shell show it's doing something instead of
    // just freezing with no feedback while the dashboard's VM graph loads (#63).
    [ObservableProperty]
    private bool _isOpeningRepo;

    /// <summary>Fire-and-forget entry point for callers that can't await (XAML bindings, delegates).</summary>
    public void OpenRepository(Repository repo) => _ = OpenRepositoryAsync(repo);

    public async Task OpenRepositoryAsync(Repository repo)
    {
        var gitService = new GitLoom.Core.Services.GitService();
        if (!gitService.IsGitRepository(repo.Path))
        {
            InvalidRepository = repo;
            IsInvalidRepoCardVisible = true;
            return;
        }

        IsOpeningRepo = true;
        try
        {
            // RepoDashboardViewModel's constructor kicks off its initial load via Dispatcher-marshalled
            // work rather than ambient SynchronizationContext capture, so building the whole VM graph
            // (including the branch/author/path enumeration that used to block the UI thread here) off
            // the UI thread is safe and keeps the shell responsive while a repo loads.
            var dashboard = await Task.Run(() => new RepoDashboardViewModel(repo,
                // The callback lets the submodules panel (T-16) open a submodule as its own
                // top-level repository through the normal open path.
                openRepositoryPath: path => OpenRepository(
                    new Repository { Path = path, DisplayName = Path.GetFileName(path.TrimEnd('/', '\\')) })));

            CurrentWorkspace = dashboard;

            // Hand the full width to the repo workspace once it's open (#61) — still toggleable back.
            if (IsSidebarOpen) ToggleSidebar();

            _settingsService.Update(p => p.LastOpenedRepoPath = repo.Path);
            IsReopenRepoCardVisible = false;
        }
        finally
        {
            IsOpeningRepo = false;
        }
    }

    [RelayCommand]
    private void CloseRepository()
    {
        CurrentWorkspace = null;
    }

    [RelayCommand]
    public void OpenAnalytics(Repository repo)
    {
        var gitService = new GitLoom.Core.Services.GitService();
        if (!gitService.IsGitRepository(repo.Path))
        {
            InvalidRepository = repo;
            IsInvalidRepoCardVisible = true;
            return;
        }

        // Load the analytics workspace
        CurrentWorkspace = new AnalyticsViewModel(repo.Path);
    }

    [RelayCommand]
    public void OpenCloudSync()
    {
        var cloneDashboard = new CloneDashboardViewModel();

        // Wire up the dialog
        cloneDashboard.ShowDeviceFlowDialogAction = (deviceFlow) =>
        {
            Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
                {
                    var dialog = new DeviceFlowAuthDialog(deviceFlow.VerificationUri, deviceFlow.UserCode);

                    cloneDashboard.CloseDeviceFlowDialogAction = () =>
                    {
                        Dispatcher.UIThread.Post(() => dialog.Close());
                    };

                    dialog.Closed += (s, e) =>
                    {
                        if (cloneDashboard.CancelLoginCommand.CanExecute(null))
                        {
                            cloneDashboard.CancelLoginCommand.Execute(null);
                        }
                    };

                    await dialog.ShowDialog(desktop.MainWindow);
                }
            });
        };

        cloneDashboard.OnCloneRequested = async (repo) =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
            {
                var folder = await desktop.MainWindow.StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "Select Clone Destination",
                    AllowMultiple = false
                });

                if (folder.Count > 0)
                {
                    var targetFolder = System.IO.Path.Combine(folder[0].Path.LocalPath, repo.Name);

                    // T-21: clone through the progress-reporting, cancellable ICloneService (a cancelled
                    // clone deletes the partial directory). The bar/cancel live on the CloneDashboard.
                    var ok = await cloneDashboard.RunCloneAsync(repo.CloneUrl, targetFolder);
                    if (ok)
                    {
                        var cat = Categories.FirstOrDefault(c => c.Name == "Uncategorized");
                        if (cat != null)
                        {
                            cat.Repositories.Add(new Repository { DisplayName = repo.Name, Path = targetFolder });
                        }
                        cloneDashboard.StatusMessage = $"Successfully cloned {repo.Name}!";
                    }
                }
            }
        };

        CurrentWorkspace = cloneDashboard;
    }

    public MainWindowViewModel()
    {
        RegisterActions();
        CommandPalette = new CommandPaletteViewModel(BuildPaletteEntries);
        CommandPalette.RequestClose += () => IsCommandPaletteOpen = false;

        SidebarColumnWidth = new Avalonia.Controls.GridLength(_settingsService.Current.SidebarWidth, Avalonia.Controls.GridUnitType.Pixel);
        AutoDetectPath = _settingsService.Current.AutoDetectPath;
        LoadCategories();

        var lastRepoPath = _settingsService.Current.LastOpenedRepoPath;

        if (!string.IsNullOrEmpty(lastRepoPath) && Directory.Exists(lastRepoPath))
        {
            ReopenRepositoryPath = lastRepoPath;
            IsReopenRepoCardVisible = true;
        }

        if (HasAutoDetectPath)
        {
            ScanAutoDetectFolderAsync().ContinueWith(_ => { });
        }
    }

    [RelayCommand]
    private void DismissReopenRepoCard()
    {
        IsReopenRepoCardVisible = false;
    }

    [RelayCommand]
    private void ReopenLastRepo()
    {
        IsReopenRepoCardVisible = false;
        var repo = Categories.SelectMany(c => c.Repositories).FirstOrDefault(r => r.Path == ReopenRepositoryPath);
        if (repo != null)
        {
            OpenRepository(repo);
        }
        else
        {
            var newRepo = new Repository { Path = ReopenRepositoryPath, DisplayName = Path.GetFileName(ReopenRepositoryPath) };
            OpenRepository(newRepo);
        }
    }

    private void LoadCategories()
    {
        using var dbContext = new AppDbContext();
        var allCategories = dbContext.WorkspaceCategories
            .Include(c => c.Repositories)
            .ToList();

        var rootCategories = allCategories
            .Where(c => c.ParentCategoryId == null)
            .OrderBy(c => c.DisplayOrder)
            .ToList();

        Categories.Clear();
        foreach (var cat in rootCategories)
        {
            cat.Repositories = new ObservableCollection<Repository>(cat.Repositories);
            SetupCategory(cat);
        }
    }

    private void SetupCategory(WorkspaceCategory cat)
    {
        cat.IsExpanded = GitLoom.App.App.Settings.Current.SidebarExpandedStates.GetValueOrDefault("Workspace_" + cat.Name, false);
        cat.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceCategory.IsExpanded))
            {
                GitLoom.App.App.Settings.Update(p => p.SidebarExpandedStates["Workspace_" + cat.Name] = cat.IsExpanded);
            }
        };

        if (cat.SubCategories != null)
        {
            cat.SubCategories = new ObservableCollection<WorkspaceCategory>(cat.SubCategories);
            foreach (var sub in cat.SubCategories)
            {
                sub.Repositories = new ObservableCollection<Repository>(sub.Repositories);
                SetupCategorySub(sub);
            }
        }
        else
        {
            cat.SubCategories = new ObservableCollection<WorkspaceCategory>();
        }

        Categories.Add(cat);
    }

    private void SetupCategorySub(WorkspaceCategory cat)
    {
        cat.IsExpanded = GitLoom.App.App.Settings.Current.SidebarExpandedStates.GetValueOrDefault("Workspace_" + cat.Name, false);
        cat.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(WorkspaceCategory.IsExpanded))
            {
                GitLoom.App.App.Settings.Update(p => p.SidebarExpandedStates["Workspace_" + cat.Name] = cat.IsExpanded);
            }
        };
    }

    [RelayCommand]
    private void CreateCategory()
    {
        using var dbContext = new AppDbContext();
        var newCat = new WorkspaceCategory { Name = "New Category", DisplayOrder = Categories.Count };
        dbContext.WorkspaceCategories.Add(newCat);
        dbContext.SaveChanges();

        newCat.IsEditingName = true;
        SetupCategory(newCat);
    }

    [RelayCommand]
    private void CreateSubCategory(WorkspaceCategory parentCat)
    {
        using var dbContext = new AppDbContext();
        var newSubCat = new WorkspaceCategory { Name = "New Sub-Category", DisplayOrder = parentCat.SubCategories?.Count ?? 0, ParentCategoryId = parentCat.CategoryId };
        dbContext.WorkspaceCategories.Add(newSubCat);
        dbContext.SaveChanges();

        LoadCategories();

        var loadedParent = FindCategoryById(parentCat.CategoryId, Categories);
        if (loadedParent != null)
        {
            loadedParent.IsExpanded = true;
            var sub = loadedParent.SubCategories.FirstOrDefault(s => s.CategoryId == newSubCat.CategoryId);
            if (sub != null)
            {
                sub.IsEditingName = true;
            }
        }
    }

    private WorkspaceCategory? FindCategoryById(int id, IEnumerable<WorkspaceCategory> list)
    {
        foreach (var cat in list)
        {
            if (cat.CategoryId == id) return cat;
            if (cat.SubCategories != null)
            {
                var found = FindCategoryById(id, cat.SubCategories);
                if (found != null) return found;
            }
        }
        return null;
    }

    [RelayCommand]
    private void RenameCategory(WorkspaceCategory cat)
    {
        cat.IsEditingName = true;
    }

    [RelayCommand]
    private void SaveCategoryName(WorkspaceCategory cat)
    {
        cat.IsEditingName = false;
        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Find(cat.CategoryId);
        if (dbCat != null)
        {
            dbCat.Name = cat.Name;
            dbContext.SaveChanges();
        }
    }

    [RelayCommand]
    private void CancelCategoryName(WorkspaceCategory cat)
    {
        cat.IsEditingName = false;
        if (cat.Name == "New Category" || cat.Name == "New Sub-Category")
        {
            // User cancelled creating a new category, delete it silently
            ExecuteDeleteCategory(cat);
        }
    }

    [RelayCommand]
    private void DeleteCategory(WorkspaceCategory cat)
    {
        _nodeToDelete = cat;
        DeleteConfirmationTitle = "Delete Category";
        DeleteConfirmationMessage = $"Are you sure you want to delete the category '{cat.Name}' and all its contents?";
        IsDeleteConfirmationVisible = true;
    }

    private void ExecuteDeleteCategory(WorkspaceCategory cat)
    {
        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Include(c => c.Repositories).FirstOrDefault(c => c.CategoryId == cat.CategoryId);
        if (dbCat != null)
        {
            // Move its repos to the first available category if any
            var otherCat = dbContext.WorkspaceCategories.FirstOrDefault(c => c.CategoryId != cat.CategoryId && c.ParentCategoryId == null);
            if (otherCat != null)
            {
                foreach (var r in dbCat.Repositories.ToList())
                {
                    r.CategoryId = otherCat.CategoryId;
                }
            }
            else if (dbCat.Repositories.Any())
            {
                // Cannot delete the only category if it has repos
                return;
            }
            dbContext.WorkspaceCategories.Remove(dbCat);
            dbContext.SaveChanges();
            LoadCategories();
        }
    }

    [RelayCommand]
    private async Task AddRepositoryToCategoryAsync(WorkspaceCategory category)
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Select Git Repository for {category.Name}",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                var gitService = new GitLoom.Core.Services.GitService();

                if (gitService.IsGitRepository(path))
                {
                    using var dbContext = new AppDbContext();
                    if (await dbContext.Repositories.AnyAsync(r => r.Path == path)) return;

                    var repo = new Repository
                    {
                        Path = path,
                        DisplayName = Path.GetFileName(path),
                        CategoryId = category.CategoryId,
                        LastAccessed = System.DateTime.UtcNow
                    };

                    dbContext.Repositories.Add(repo);
                    await dbContext.SaveChangesAsync();
                    LoadCategories(); // Refresh Sidebar
                }
            }
        }
    }

    [RelayCommand]
    private async Task MoveRepositoryAsync(Repository repo)
    {
        using var dbContext = new AppDbContext();

        // Find the OTHER category. If it's in Personal, find Work. If Work, find Personal.
        var targetCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync(c => c.CategoryId != repo.CategoryId);

        if (targetCategory != null)
        {
            var dbRepo = await dbContext.Repositories.FindAsync(repo.RepositoryId);
            if (dbRepo != null)
            {
                dbRepo.CategoryId = targetCategory.CategoryId;
                await dbContext.SaveChangesAsync();
                LoadCategories();
            }
        }
    }

    public void MoveRepositoryToCategory(Repository repo, WorkspaceCategory targetCategory)
    {
        if (repo.CategoryId == targetCategory.CategoryId) return; // Already there!

        using var dbContext = new AppDbContext();
        var dbRepo = dbContext.Repositories.Find(repo.RepositoryId);

        if (dbRepo != null)
        {
            dbRepo.CategoryId = targetCategory.CategoryId;
            dbContext.SaveChanges();
            LoadCategories();
        }
    }

    public void MoveCategoryToCategory(WorkspaceCategory source, WorkspaceCategory? target)
    {
        if (target != null && source.CategoryId == target.CategoryId) return;
        if (target != null && target.ParentCategoryId == source.CategoryId) return; // Can't move parent into child

        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Find(source.CategoryId);
        if (dbCat != null)
        {
            dbCat.ParentCategoryId = target?.CategoryId;
            dbContext.SaveChanges();
            LoadCategories();
        }
    }

    [RelayCommand]
    private void RemoveRepository(Repository repo)
    {
        _nodeToDelete = repo;
        DeleteConfirmationTitle = "Remove Repository";
        DeleteConfirmationMessage = $"Are you sure you want to remove '{repo.DisplayName}' from GitLoom? (Your local files will not be deleted)";
        IsDeleteConfirmationVisible = true;
    }

    private async Task ExecuteRemoveRepositoryAsync(Repository repo)
    {
        using var dbContext = new AppDbContext();
        var dbRepo = await dbContext.Repositories.FindAsync(repo.RepositoryId);
        if (dbRepo != null)
        {
            dbContext.Repositories.Remove(dbRepo);
            await dbContext.SaveChangesAsync();
            LoadCategories();

            // If they removed the repo they are currently looking at, close the dashboard!
            if (CurrentWorkspace is RepoDashboardViewModel rvm && rvm.RepositoryName == repo.DisplayName)
            {
                CurrentWorkspace = null;
            }
        }
    }

    [RelayCommand]
    private void CancelInvalidRepoCard()
    {
        IsInvalidRepoCardVisible = false;
        InvalidRepository = null;
    }

    [RelayCommand]
    private async Task RemoveInvalidRepoAsync()
    {
        if (InvalidRepository != null)
        {
            await ExecuteRemoveRepositoryAsync(InvalidRepository);
            CancelInvalidRepoCard();
        }
    }

    [RelayCommand]
    private async Task ChangeInvalidRepoPathAsync()
    {
        if (InvalidRepository == null) return;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = $"Select new Git Repository location for {InvalidRepository.DisplayName}",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                var gitService = new GitLoom.Core.Services.GitService();

                if (gitService.IsGitRepository(path))
                {
                    using var dbContext = new AppDbContext();
                    var dbRepo = await dbContext.Repositories.FindAsync(InvalidRepository.RepositoryId);
                    if (dbRepo != null)
                    {
                        dbRepo.Path = path;
                        dbRepo.DisplayName = Path.GetFileName(path);
                        await dbContext.SaveChangesAsync();

                        LoadCategories(); // Refresh Sidebar

                        var updatedRepo = dbRepo;
                        CancelInvalidRepoCard();
                        OpenRepository(updatedRepo);
                    }
                }
            }
        }
    }

    [RelayCommand]
    private void SetRepoColorRed(Repository repo) => SetRepoColor(repo, "#FF5252");
    [RelayCommand]
    private void SetRepoColorGreen(Repository repo) => SetRepoColor(repo, "#4CAF50");
    [RelayCommand]
    private void SetRepoColorBlue(Repository repo) => SetRepoColor(repo, "#569CD6");
    [RelayCommand]
    private void SetRepoColorYellow(Repository repo) => SetRepoColor(repo, "#FFEB3B");
    [RelayCommand]
    private void SetRepoColorPurple(Repository repo) => SetRepoColor(repo, "#9C27B0");

    private void SetRepoColor(Repository repo, string hexColor)
    {
        repo.CustomIconColor = hexColor;
        using var db = new AppDbContext();
        var dbRepo = db.Repositories.Find(repo.RepositoryId);
        if (dbRepo != null)
        {
            dbRepo.CustomIconColor = hexColor;
            db.SaveChanges();
        }
    }

    [RelayCommand]
    private async Task SelectAutoDetectFolderAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select folder for auto-detecting repositories",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                AutoDetectPath = result[0].Path.LocalPath;
                _settingsService.Update(p => p.AutoDetectPath = AutoDetectPath);
                await ScanAutoDetectFolderAsync();
            }
        }
    }

    [ObservableProperty]
    private bool _isScanning;

    [RelayCommand]
    private async Task ScanAutoDetectFolderAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        var minTask = Task.Delay(1000);

        try
        {
            if (string.IsNullOrEmpty(AutoDetectPath) || !Directory.Exists(AutoDetectPath)) return;

            var gitService = new GitLoom.Core.Services.GitService();
            using var dbContext = new AppDbContext();

            var defaultCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync(c => c.Name == "Personal")
                                ?? await dbContext.WorkspaceCategories.FirstOrDefaultAsync();

            if (defaultCategory == null) return;

            var dirs = Directory.GetDirectories(AutoDetectPath);
            bool anyAdded = false;

            foreach (var dir in dirs)
            {
                if (gitService.IsGitRepository(dir))
                {
                    if (!await dbContext.Repositories.AnyAsync(r => r.Path == dir))
                    {
                        var repo = new Repository
                        {
                            Path = dir,
                            DisplayName = Path.GetFileName(dir),
                            CategoryId = defaultCategory.CategoryId,
                            LastAccessed = System.DateTime.UtcNow
                        };
                        dbContext.Repositories.Add(repo);
                        anyAdded = true;
                    }
                }
                else
                {
                    try
                    {
                        var subdirs = Directory.GetDirectories(dir);
                        bool categoryCreated = false;
                        WorkspaceCategory? curCategory = null;

                        foreach (var subdir in subdirs)
                        {
                            if (gitService.IsGitRepository(subdir))
                            {
                                if (!await dbContext.Repositories.AnyAsync(r => r.Path == subdir))
                                {
                                    if (!categoryCreated)
                                    {
                                        curCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync(c => c.Name == Path.GetFileName(dir));
                                        if (curCategory == null)
                                        {
                                            curCategory = new WorkspaceCategory { Name = Path.GetFileName(dir) };
                                            dbContext.WorkspaceCategories.Add(curCategory);
                                            await dbContext.SaveChangesAsync();
                                        }
                                        categoryCreated = true;
                                    }

                                    var repo = new Repository
                                    {
                                        Path = subdir,
                                        DisplayName = Path.GetFileName(subdir),
                                        CategoryId = curCategory!.CategoryId,
                                        LastAccessed = System.DateTime.UtcNow
                                    };
                                    dbContext.Repositories.Add(repo);
                                    anyAdded = true;
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            if (anyAdded)
            {
                await dbContext.SaveChangesAsync();
                LoadCategories();
            }
        }
        finally
        {
            await minTask;
            IsScanning = false;
        }
    }

    [RelayCommand]
    private async Task CreateGitRepositoryAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var storageProvider = desktop.MainWindow.StorageProvider;
            var result = await storageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Select Empty Folder to Initialize Git Repository",
                AllowMultiple = false
            });

            if (result.Count > 0)
            {
                string path = result[0].Path.LocalPath;
                try
                {
                    LibGit2Sharp.Repository.Init(path);

                    using var dbContext = new AppDbContext();
                    if (!await dbContext.Repositories.AnyAsync(r => r.Path == path))
                    {
                        var defaultCategory = await dbContext.WorkspaceCategories.FirstOrDefaultAsync();
                        if (defaultCategory != null)
                        {
                            var repo = new Repository
                            {
                                Path = path,
                                DisplayName = Path.GetFileName(path),
                                CategoryId = defaultCategory.CategoryId,
                                LastAccessed = System.DateTime.UtcNow
                            };
                            dbContext.Repositories.Add(repo);
                            await dbContext.SaveChangesAsync();
                            LoadCategories();
                            OpenRepository(repo);
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    // Initialization failed
                    System.Console.WriteLine("Git Init Failed: " + ex.Message);
                }
            }
        }
    }

    [RelayCommand]
    private async Task ChangeRepoIconColorAsync(Repository repo)
    {
        // For simplicity, cycle through some nice colors: Cyan, Red, Green, Purple, Yellow
        var colors = new[] { "#00CED1", "#FF5C5C", "#4CAF50", "#9C27B0", "#FFC107" };
        var currentColor = repo.CustomIconColor;
        var nextColor = colors[0];

        var index = System.Array.IndexOf(colors, currentColor);
        if (index >= 0 && index < colors.Length - 1)
            nextColor = colors[index + 1];
        else if (index == colors.Length - 1)
            nextColor = colors[0];

        repo.CustomIconColor = nextColor;

        using var dbContext = new AppDbContext();
        var dbRepo = await dbContext.Repositories.FindAsync(repo.RepositoryId);
        if (dbRepo != null)
        {
            dbRepo.CustomIconColor = nextColor;
            await dbContext.SaveChangesAsync();
            LoadCategories();
        }
    }
}
