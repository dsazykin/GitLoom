using System;
using System.IO;
using System.Linq;
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

    private void CommitFile(string relPath, string content, string message)
    {
        File.WriteAllText(Path.Combine(_tempPath, relPath), content);
        using var repo = new Repository(_tempPath);
        repo.Config.Set("user.name", "Test User");
        repo.Config.Set("user.email", "test@example.com");
        Commands.Stage(repo, relPath);
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }

    [Fact]
    public void GetRecentCommits_MultiPathFilter_ReturnsOnlyTouchingCommitsDeduped()
    {
        // Regression for audit 1.8: the multi-path filter must return each commit
        // touching ANY requested path exactly once (no duplicates), in history
        // order, and exclude commits touching only other paths.
        var service = new GitService();
        Repository.Init(_tempPath);
        CommitFile("a.txt", "a1", "add a");
        CommitFile("b.txt", "b1", "add b");
        CommitFile("a.txt", "a2", "modify a");
        CommitFile("c.txt", "c1", "add c");

        var filter = new GitLoom.Core.Models.CommitSearchFilter
        {
            FilePaths = new System.Collections.Generic.List<string> { "a.txt", "b.txt" }
        };
        var commits = service.GetRecentCommits(_tempPath, 0, 100, filter).ToList();

        var messages = commits.Select(c => c.MessageShort).ToList();
        Assert.Equal(3, commits.Count);
        Assert.Equal(commits.Count, commits.Select(c => c.Sha).Distinct().Count());
        Assert.Contains("modify a", messages);
        Assert.Contains("add a", messages);
        Assert.Contains("add b", messages);
        Assert.DoesNotContain("add c", messages);
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
