using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core;
using GitLoom.Core.Models;
using Microsoft.EntityFrameworkCore;

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
    private void AddRepository()
    {
        // TODO in Phase 1.5 Part 2: Open Avalonia folder picker
        // and save the discovered .git path to the database.
    }
}