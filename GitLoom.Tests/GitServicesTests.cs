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

    [Fact]
    public void Commit_ShouldResolveLocalIdentity_ThroughGetSignature()
    {
        // Regression for audit 1.2: a locally-configured identity must be
        // resolved through GetSignature and used for the commit.
        var service = new GitService();
        Repository.Init(_tempPath);
        using (var repo = new Repository(_tempPath))
        {
            repo.Config.Set("user.name", "Test User");
            repo.Config.Set("user.email", "test@example.com");
        }

        File.WriteAllText(Path.Combine(_tempPath, "a.txt"), "hello");
        service.StageFile(_tempPath, "a.txt");
        service.Commit(_tempPath, "initial commit");

        using (var repo = new Repository(_tempPath))
        {
            Assert.Single(repo.Commits);
            Assert.Equal("Test User", repo.Head.Tip.Author.Name);
            Assert.Equal("test@example.com", repo.Head.Tip.Author.Email);
        }
    }

    [Fact]
    public void Commit_ShouldThrowGitIdentityMissing_WhenNoIdentityConfigured()
    {
        // Regression for audit 1.2: with NO identity anywhere (no local and no
        // global/xdg/system), Commit must throw a typed GitIdentityMissingException
        // rather than crashing with an NRE or committing a placeholder identity.
        // Point every non-local config level at an empty dir so the developer's
        // real global gitconfig can't satisfy the identity and mask the throw.
        var emptyConfigDir = Path.Combine(Path.GetTempPath(), "GitLoomNoCfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyConfigDir);
        LibGit2Sharp.GlobalSettings.SetConfigSearchPaths(LibGit2Sharp.ConfigurationLevel.Global, emptyConfigDir);
        LibGit2Sharp.GlobalSettings.SetConfigSearchPaths(LibGit2Sharp.ConfigurationLevel.Xdg, emptyConfigDir);
        LibGit2Sharp.GlobalSettings.SetConfigSearchPaths(LibGit2Sharp.ConfigurationLevel.System, emptyConfigDir);
        try
        {
            var service = new GitService();
            Repository.Init(_tempPath);
            File.WriteAllText(Path.Combine(_tempPath, "a.txt"), "hello");
            service.StageFile(_tempPath, "a.txt");

            Assert.Throws<GitLoom.Core.Exceptions.GitIdentityMissingException>(
                () => service.Commit(_tempPath, "initial commit"));
        }
        finally
        {
            // Reset the search paths (null restores libgit2's defaults).
            LibGit2Sharp.GlobalSettings.SetConfigSearchPaths(LibGit2Sharp.ConfigurationLevel.Global, null);
            LibGit2Sharp.GlobalSettings.SetConfigSearchPaths(LibGit2Sharp.ConfigurationLevel.Xdg, null);
            LibGit2Sharp.GlobalSettings.SetConfigSearchPaths(LibGit2Sharp.ConfigurationLevel.System, null);
            try { Directory.Delete(emptyConfigDir, true); } catch { }
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
