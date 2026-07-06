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
    public DbSet<PinnedRef> PinnedRefs { get; set; } = null!;
    public DbSet<JournalEntry> JournalEntries { get; set; } = null!;
    public DbSet<GitProfile> GitProfiles { get; set; } = null!;

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

        // Pinned refs (T-09): keyed by Id, one row per (repo, ref).
        modelBuilder.Entity<PinnedRef>().HasKey(p => p.Id);
        modelBuilder.Entity<PinnedRef>()
            .HasIndex(p => new { p.RepoPath, p.RefName })
            .IsUnique();

        // Operation journal (T-19): one row per mutating op, queried per repo newest-first.
        modelBuilder.Entity<JournalEntry>().HasKey(j => j.Id);
        modelBuilder.Entity<JournalEntry>()
            .HasIndex(j => j.RepoPath);

        // Git identity profiles (T-21): keyed by Id; names are unique (case-insensitive enforced in
        // ProfileService — a plain unique index would be case-sensitive under SQLite's default collation).
        modelBuilder.Entity<GitProfile>().HasKey(p => p.Id);

        // Seed some initial default categories
        modelBuilder.Entity<WorkspaceCategory>().HasData(
            new WorkspaceCategory { CategoryId = 1, Name = "Personal", DisplayOrder = 1 },
            new WorkspaceCategory { CategoryId = 2, Name = "Work", DisplayOrder = 2 }
        );
    }
}
