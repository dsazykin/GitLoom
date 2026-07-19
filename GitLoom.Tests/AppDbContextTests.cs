using System;
using System.IO;
using System.Linq;
using GitLoom.Core;
using Mainguard.Git.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

using Mainguard.Git;
namespace GitLoom.Tests;

public class AppDbContextTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AppDbContextTests()
    {
        // Set up in-memory SQLite database connection
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .Options;

        // Initialize schema
        using var context = new AppDbContext(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Database_ShouldSeedDefaultCategories()
    {
        // Arrange & Act
        using var context = new AppDbContext(_options);
        var categories = context.WorkspaceCategories.ToList();

        // Assert
        Assert.Equal(2, categories.Count);
        Assert.Contains(categories, c => c.Name == "Personal");
        Assert.Contains(categories, c => c.Name == "Work");
    }

    [Fact]
    public void Database_ShouldSaveRepositoryAndCategory()
    {
        // Arrange
        using (var context = new AppDbContext(_options))
        {
            var category = new WorkspaceCategory { Name = "Projects", DisplayOrder = 3 };
            context.WorkspaceCategories.Add(category);
            context.SaveChanges();

            var repo = new Repository
            {
                DisplayName = "GitLoom Repo",
                Path = "/Users/test/GitLoom",
                CategoryId = category.CategoryId
            };
            context.Repositories.Add(repo);
            context.SaveChanges();
        }

        // Act & Assert
        using (var context = new AppDbContext(_options))
        {
            var savedRepo = context.Repositories
                .Include(r => r.Category)
                .FirstOrDefault(r => r.DisplayName == "GitLoom Repo");

            Assert.NotNull(savedRepo);
            Assert.Equal("/Users/test/GitLoom", savedRepo.Path);
            Assert.NotNull(savedRepo.Category);
            Assert.Equal("Projects", savedRepo.Category.Name);
        }
    }

    [Fact]
    public void Database_ShouldEnforceUniquePathConstraint()
    {
        // Arrange
        using var context = new AppDbContext(_options);
        var repo1 = new Repository { DisplayName = "Repo 1", Path = "/same/path", CategoryId = 1 };
        var repo2 = new Repository { DisplayName = "Repo 2", Path = "/same/path", CategoryId = 1 };

        context.Repositories.Add(repo1);
        context.SaveChanges();

        context.Repositories.Add(repo2);

        // Act & Assert
        Assert.ThrowsAny<DbUpdateException>(() => context.SaveChanges());
    }

    [Fact]
    public void Database_ShouldCascadeDeleteRepositories_WhenCategoryIsDeleted()
    {
        // Arrange
        int categoryId;
        using (var context = new AppDbContext(_options))
        {
            var category = new WorkspaceCategory { Name = "ToDelete", DisplayOrder = 4 };
            context.WorkspaceCategories.Add(category);
            context.SaveChanges();
            categoryId = category.CategoryId;

            var repo = new Repository { DisplayName = "Cascade Test", Path = "/cascade/path", CategoryId = categoryId };
            context.Repositories.Add(repo);
            context.SaveChanges();
        }

        // Act
        using (var context = new AppDbContext(_options))
        {
            var category = context.WorkspaceCategories.Find(categoryId);
            Assert.NotNull(category);
            context.WorkspaceCategories.Remove(category);
            context.SaveChanges();
        }

        // Assert
        using (var context = new AppDbContext(_options))
        {
            var repo = context.Repositories.FirstOrDefault(r => r.DisplayName == "Cascade Test");
            Assert.Null(repo);
        }
    }
}
