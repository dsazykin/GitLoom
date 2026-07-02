using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.Core.Services;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

public class GitServiceTests : IDisposable
{
    private readonly string _tempPath;

    public GitServiceTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), "GitLoomTests_" +
Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempPath))
        {
            try
            {
                // Force delete read-only files inside .git if any exist
                DeleteDirectoryWithForce(_tempPath);
            }
            catch
            {
                // Silence cleanup errors in temp folder
            }
        }
    }

    private static void DeleteDirectoryWithForce(string path)
    {
        foreach (var directory in Directory.GetDirectories(path))
        {
            DeleteDirectoryWithForce(directory);
        }

        foreach (var file in Directory.GetFiles(path))
        {
            var fileInfo = new FileInfo(file)
            {
                Attributes = FileAttributes.Normal
            };
            fileInfo.Delete();
        }

        Directory.Delete(path, false);
    }

    [Fact]
    public void IsGitRepository_ShouldReturnFalse_ForNonGitPath()
    {
        var service = new GitService();
        var result = service.IsGitRepository(_tempPath);
        Assert.False(result);
    }

    [Fact]
    public void IsGitRepository_ShouldReturnTrue_ForGitPath()
    {
        var service = new GitService();
        Repository.Init(_tempPath);

        var result = service.IsGitRepository(_tempPath);
        Assert.True(result);
    }

    [Fact]
    public void ExecuteWithRepo_ShouldProvideRepositoryAndDispose()
    {
        var service = new GitService();
        Repository.Init(_tempPath);

        bool wasCalled = false;
        service.ExecuteWithRepo(_tempPath, repo =>
        {
            Assert.NotNull(repo);
            wasCalled = true;
        });

        Assert.True(wasCalled);
    }

    private void ConfigureIdentity()
    {
        using var repo = new Repository(_tempPath);
        repo.Config.Set("user.name", "Test User");
        repo.Config.Set("user.email", "test@example.com");
    }

    [Fact]
    public void DiscardChanges_ShouldNotDelete_UntrackedDirectory()
    {
        // Regression for audit 1.4: discard must never recursively delete a
        // directory, which would silently wipe an untracked folder of work.
        var service = new GitService();
        Repository.Init(_tempPath);

        var dir = Path.Combine(_tempPath, "untracked_dir");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "precious.txt"), "do not delete");

        service.DiscardChanges(_tempPath, new[] { "untracked_dir" });

        Assert.True(Directory.Exists(dir), "Untracked directory must not be deleted by discard.");
        Assert.True(File.Exists(Path.Combine(dir, "precious.txt")));
    }

    [Fact]
    public void DiscardChanges_ShouldRemove_UntrackedFile()
    {
        var service = new GitService();
        Repository.Init(_tempPath);

        var file = Path.Combine(_tempPath, "junk.txt");
        File.WriteAllText(file, "temporary");

        service.DiscardChanges(_tempPath, new[] { "junk.txt" });

        Assert.False(File.Exists(file), "Untracked file should be removed by discard.");
    }

    [Fact]
    public void DiscardChanges_ShouldRestore_ModifiedTrackedFile()
    {
        var service = new GitService();
        Repository.Init(_tempPath);
        ConfigureIdentity();

        var file = Path.Combine(_tempPath, "tracked.txt");
        File.WriteAllText(file, "original");
        service.StageFile(_tempPath, "tracked.txt");
        using (var repo = new Repository(_tempPath))
        {
            var sig = repo.Config.BuildSignature(DateTimeOffset.Now);
            repo.Commit("add tracked", sig, sig);
        }

        File.WriteAllText(file, "modified");
        service.DiscardChanges(_tempPath, new[] { "tracked.txt" });

        Assert.Equal("original", File.ReadAllText(file));
    }

    [Fact]
    public async Task
RepositoryWatcher_ShouldTrigger_OnIndexModification()
    {
        Repository.Init(_tempPath);
        var tcs = new TaskCompletionSource<bool>();

        // 100ms debounce for quick test execution
        using var watcher = new RepositoryWatcher(_tempPath, 100);
        watcher.RepositoryChanged += () => tcs.TrySetResult(true);

        // Modify the .git/index file (or refs) to trigger the watcher
        var indexFile = Path.Combine(_tempPath, ".git", "index");
        await File.WriteAllTextAsync(indexFile, "test content");

        // Wait with a timeout
        var completedTask = await Task.WhenAny(tcs.Task, Task.
Delay(2000));
        Assert.Same(tcs.Task, completedTask);
        Assert.True(await tcs.Task);
    }
}
