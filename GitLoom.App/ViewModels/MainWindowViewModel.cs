using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core;
using GitLoom.Core.Models;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using GitLoom.App.Views;

namespace GitLoom.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<WorkspaceCategory> _categories =
        new();
    
    [ObservableProperty]
    private ViewModelBase? _currentWorkspace;

    [ObservableProperty]
    private object? _selectedNode;
    
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

    private readonly GitLoom.Core.Services.SettingsService _settingsService = new GitLoom.Core.Services.SettingsService();

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
    }

    [ObservableProperty]
    private bool _isCommandPaletteOpen;

    [ObservableProperty]
    private string _commandPaletteSearchText = string.Empty;

    partial void OnCommandPaletteSearchTextChanged(string value)
    {
        UpdateCommandPaletteSearch();
    }

    [ObservableProperty]
    private ObservableCollection<Repository> _commandPaletteSearchResults = new();

    [RelayCommand]
    private void OpenCommandPalette()
    {
        CommandPaletteSearchText = string.Empty;
        UpdateCommandPaletteSearch();
        IsCommandPaletteOpen = true;
    }

    [RelayCommand]
    private void CloseCommandPalette()
    {
        IsCommandPaletteOpen = false;
    }

    private void UpdateCommandPaletteSearch()
    {
        var allRepos = Categories.SelectMany(c => c.Repositories).ToList();
        if (string.IsNullOrWhiteSpace(CommandPaletteSearchText))
        {
            CommandPaletteSearchResults = new ObservableCollection<Repository>(allRepos.OrderBy(r => r.DisplayName));
        }
        else
        {
            var search = CommandPaletteSearchText.ToLowerInvariant();
            CommandPaletteSearchResults = new ObservableCollection<Repository>(
                allRepos.Where(r => r.DisplayName.ToLowerInvariant().Contains(search) || r.Path.ToLowerInvariant().Contains(search))
                        .OrderBy(r => r.DisplayName));
        }
    }

    [RelayCommand]
    private void SelectCommandPaletteRepo(Repository? repo)
    {
        if (repo != null)
        {
            OpenRepository(repo);
            IsCommandPaletteOpen = false;
        }
    }

    // This is automatically triggered by the MVVM Toolkit whenever _selectedNode changes!
    partial void OnSelectedNodeChanged(object? value)
    {
        // Intentionally empty: we no longer auto-open on selection to allow right-clicks without closing the sidebar.
    }

    public void OpenRepository(Repository repo)
    {
        var gitService = new GitLoom.Core.Services.GitService();
        if (!gitService.IsGitRepository(repo.Path))
        {
            InvalidRepository = repo;
            IsInvalidRepoCardVisible = true;
            return;
        }

        // Load the dashboard
        CurrentWorkspace = new RepoDashboardViewModel(repo);

        _settingsService.Update(p => p.LastOpenedRepoPath = repo.Path);
        IsReopenRepoCardVisible = false;
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
                    
                    cloneDashboard.CloseDeviceFlowDialogAction = () => { 
                        Dispatcher.UIThread.Post(() => dialog.Close()); 
                    };
                    
                    dialog.Closed += (s, e) => {
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
                    try
                    {
                        cloneDashboard.IsLoading = true;
                        cloneDashboard.StatusMessage = $"Cloning {repo.Name}...";
                        await System.Threading.Tasks.Task.Run(() =>
                        {
                            LibGit2Sharp.Repository.Clone(repo.CloneUrl, targetFolder);
                        });

                        var cat = Categories.FirstOrDefault(c => c.Name == "Uncategorized");
                        if (cat != null)
                        {
                            cat.Repositories.Add(new Repository { DisplayName = repo.Name, Path = targetFolder });
                        }
                        
                        cloneDashboard.StatusMessage = $"Successfully cloned {repo.Name}!";
                    }
                    catch (System.Exception ex)
                    {
                        cloneDashboard.StatusMessage = $"Clone failed: {ex.Message}";
                    }
                    finally
                    {
                        cloneDashboard.IsLoading = false;
                    }
                }
            }
        };

        CurrentWorkspace = cloneDashboard;
    }

    public MainWindowViewModel()
    {
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
            // User cancelled creating a new category, delete it
            DeleteCategory(cat);
        }
    }

    [RelayCommand]
    private void DeleteCategory(WorkspaceCategory cat)
    {
        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Include(c => c.Repositories).FirstOrDefault(c => c.CategoryId == cat.CategoryId);
        if (dbCat != null)
        {
            // Move its repos to the first available category if any
            var otherCat = dbContext.WorkspaceCategories.FirstOrDefault(c => c.CategoryId != cat.CategoryId && c.ParentCategoryId == null);
            if (otherCat != null)
            {
                foreach(var r in dbCat.Repositories.ToList())
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
    private async Task RemoveRepositoryAsync(Repository repo)
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
            await RemoveRepositoryAsync(InvalidRepository);
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