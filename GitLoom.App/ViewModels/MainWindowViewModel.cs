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
        private async Task AddRepositoryAsync()
        {
            // 1. Get the Avalonia Window context to open the dialog
            if (Application.Current?.ApplicationLifetime is
  IClassicDesktopStyleApplicationLifetime desktop &&
                desktop.MainWindow != null)
            {
                var storageProvider = desktop.MainWindow.StorageProvider;

                // 2. Open Native Folder Picker
                var result = await storageProvider.
  OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select Git Repository",
                    AllowMultiple = false
                });

                if (result.Count > 0)
                {
                    string path = result[0].Path.LocalPath;

                    // 3. Verify it's a Git repository
                    var gitService = new GitLoom.Core.Services.
  GitService();
                    if (gitService.IsGitRepository(path))
                    {
                        await SaveRepositoryToDatabaseAsync(path);
                    }
                }
            }
        }

        private async Task SaveRepositoryToDatabaseAsync(string path)
        {
            using var dbContext = new AppDbContext();

            // Prevent adding duplicates
            if (await dbContext.Repositories.AnyAsync(r => r.Path ==
  path))
                return;

            // Ensure we have a default "Personal" category
            var defaultCategory = await dbContext.WorkspaceCategories.
  FirstOrDefaultAsync(c => c.Name == "Personal");
            if (defaultCategory == null) return;

            var repo = new Repository
            {
                Path = path,
                DisplayName = Path.GetFileName(path), // Uses the folder name
                CategoryId = defaultCategory.CategoryId,
                LastAccessed = System.DateTime.UtcNow
            };

            dbContext.Repositories.Add(repo);
            await dbContext.SaveChangesAsync();

            // Refresh the sidebar
            LoadCategories();
        }
}