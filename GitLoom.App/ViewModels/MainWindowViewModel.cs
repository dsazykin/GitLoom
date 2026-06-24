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
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;

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

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
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

        // Auto-collapse the sidebar!
        IsSidebarOpen = false;
    }

    public MainWindowViewModel()
    {
        LoadCategories();
    }

    private void LoadCategories()
    {
        using var dbContext = new AppDbContext();

        // Load categories and their associated repositories
        var loadedCategories = dbContext.WorkspaceCategories
            .Include(c => c.Repositories)
            .OrderBy(c => c.DisplayOrder)
            .ToList();

        if (Categories.Count == 0)
        {
            foreach (var cat in loadedCategories)
            {
                cat.Repositories = new ObservableCollection<Repository>(cat.Repositories);
                Categories.Add(cat);
            }
        }
        else
        {
            // Sync existing Categories to keep expansion state
            foreach (var loadedCat in loadedCategories)
            {
                var existingCat = Categories.FirstOrDefault(c => c.CategoryId == loadedCat.CategoryId);
                if (existingCat != null)
                {
                    // Sync Repositories
                    var existingRepoIds = existingCat.Repositories.Select(r => r.RepositoryId).ToList();
                    var loadedRepoIds = loadedCat.Repositories.Select(r => r.RepositoryId).ToList();

                    // Remove missing
                    for (int i = existingCat.Repositories.Count - 1; i >= 0; i--)
                    {
                        var repo = existingCat.Repositories[i];
                        if (!loadedRepoIds.Contains(repo.RepositoryId))
                        {
                            existingCat.Repositories.RemoveAt(i);
                        }
                    }

                    // Add new
                    foreach (var repo in loadedCat.Repositories)
                    {
                        if (!existingRepoIds.Contains(repo.RepositoryId))
                        {
                            existingCat.Repositories.Add(repo);
                        }
                    }
                }
            }
        }
    }

    [RelayCommand]
    private void CreateCategory()
    {
        using var dbContext = new AppDbContext();
        var newCat = new WorkspaceCategory { Name = "New Category", DisplayOrder = Categories.Count };
        dbContext.WorkspaceCategories.Add(newCat);
        dbContext.SaveChanges();
        
        newCat.IsEditingName = true;
        Categories.Add(newCat);
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
    private void DeleteCategory(WorkspaceCategory cat)
    {
        using var dbContext = new AppDbContext();
        var dbCat = dbContext.WorkspaceCategories.Include(c => c.Repositories).FirstOrDefault(c => c.CategoryId == cat.CategoryId);
        if (dbCat != null)
        {
            // Move its repos to the first available category if any
            var otherCat = dbContext.WorkspaceCategories.FirstOrDefault(c => c.CategoryId != cat.CategoryId);
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
}