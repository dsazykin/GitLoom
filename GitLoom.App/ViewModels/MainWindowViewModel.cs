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

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarOpen = !IsSidebarOpen;
    }

    // This is automatically triggered by the MVVM Toolkit whenever _selectedNode changes!
    partial void OnSelectedNodeChanged(object? value)
    {
        if (value is Repository repo)
        {
            // Load the dashboard
            CurrentWorkspace = new RepoDashboardViewModel(repo);

            // Auto-collapse the sidebar!
            IsSidebarOpen = false;
        }
        else
        {
            CurrentWorkspace = null;
        }
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

        Categories = new
            ObservableCollection<WorkspaceCategory>(loadedCategories);
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
}