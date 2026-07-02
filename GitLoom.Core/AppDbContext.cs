using System;
using System.IO;
using GitLoom.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace GitLoom.Core;

public class AppDbContext : DbContext
{
    private readonly string? _dbPath;

    public DbSet<WorkspaceCategory> WorkspaceCategories { get; set; } = null!;
    public DbSet<Repository> Repositories { get; set; } = null!;

    public AppDbContext()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dirPath = Path.Combine(appData, "GitLoom");
        _dbPath = Path.Combine(dirPath, "gitloom.db");
    }

    public AppDbContext(string dbPath)
    {
        _dbPath = dbPath;
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            var dbPath = _dbPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GitLoom", "gitloom.db");
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Explicit primary keys
        modelBuilder.Entity<WorkspaceCategory>().HasKey(c => c.CategoryId);
        modelBuilder.Entity<Repository>().HasKey(r => r.RepositoryId);

        // Configure CategoryId as foreign key with Cascade delete
        modelBuilder.Entity<Repository>()
            .HasOne(r => r.Category)
            .WithMany(c => c.Repositories)
            .HasForeignKey(r => r.CategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<WorkspaceCategory>()
            .HasOne(c => c.ParentCategory)
            .WithMany(c => c.SubCategories)
            .HasForeignKey(c => c.ParentCategoryId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ensure Path is indexed
        modelBuilder.Entity<Repository>()
            .HasIndex(r => r.Path)
            .IsUnique();

        // Seed some initial default categories
        modelBuilder.Entity<WorkspaceCategory>().HasData(
            new WorkspaceCategory { CategoryId = 1, Name = "Personal", DisplayOrder = 1 },
            new WorkspaceCategory { CategoryId = 2, Name = "Work", DisplayOrder = 2 }
        );
    }
}
