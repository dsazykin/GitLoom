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

    private static void CommitAll(string repoPath, string message)
    {
        using var repo = new Repository(repoPath);
        repo.Config.Set("user.name", "Test User");
        repo.Config.Set("user.email", "test@example.com");
        Commands.Stage(repo, "*");
        var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
        repo.Commit(message, sig, sig);
    }

    [Fact]
    public void CheckoutBranch_ShouldCreateTrackingLocalBranch_ForRemoteBranch()
    {
        // Regression for audit 1.12: checking out a remote branch must create a
        // local branch that tracks the correct upstream (the remote ref captured
        // once, not re-indexed after reassigning the branch variable).
        var originPath = _tempPath;
        var localPath = Path.Combine(Path.GetTempPath(), "GitLoomTests_local_" + Guid.NewGuid().ToString("N"));
        try
        {
            Repository.Init(originPath);
            File.WriteAllText(Path.Combine(originPath, "file.txt"), "base\n");
            CommitAll(originPath, "base");
            using (var origin = new Repository(originPath))
            {
                var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
                var feature = origin.CreateBranch("feature");
                Commands.Checkout(origin, feature);
                File.WriteAllText(Path.Combine(originPath, "file.txt"), "feature\n");
                Commands.Stage(origin, "*");
                origin.Commit("feature work", sig, sig);
            }

            Repository.Clone(originPath, localPath);

            var service = new GitService();
            service.CheckoutBranch(localPath, "origin/feature");

            using var local = new Repository(localPath);
            var localFeature = local.Branches["feature"];
            Assert.NotNull(localFeature);
            Assert.True(localFeature.IsCurrentRepositoryHead);
            Assert.Equal("origin/feature", localFeature.TrackedBranch?.FriendlyName);
        }
        finally
        {
            if (Directory.Exists(localPath)) DeleteDirectoryWithForce(localPath);
        }
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
