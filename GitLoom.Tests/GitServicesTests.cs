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

    private static string FirstHunkOf(string patch)
    {
        var lines = patch.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        int i = 0;
        // File header up to (but excluding) the first hunk marker.
        for (; i < lines.Length && !lines[i].StartsWith("@@"); i++)
            sb.Append(lines[i]).Append('\n');
        // The first hunk marker plus its body, until the next hunk or EOF.
        if (i < lines.Length) { sb.Append(lines[i]).Append('\n'); i++; }
        for (; i < lines.Length && !lines[i].StartsWith("@@"); i++)
            sb.Append(lines[i]).Append('\n');
        return sb.ToString();
    }

    [Fact]
    public void StageHunk_ShouldStageOnlyTheSelectedHunk()
    {
        // Regression for audit 1.13: partial staging. A file with two separate
        // changed regions -> stage only the first hunk; the second must remain
        // unstaged.
        var service = new GitService();
        Repository.Init(_tempPath);

        var original = string.Join("\n", Enumerable.Range(1, 12).Select(n => $"L{n}")) + "\n";
        var file = Path.Combine(_tempPath, "f.txt");
        File.WriteAllText(file, original);
        service.StageFile(_tempPath, "f.txt");
        using (var repo = new Repository(_tempPath))
        {
            var sig = new Signature("Test User", "test@example.com", DateTimeOffset.Now);
            repo.Config.Set("user.name", "Test User");
            repo.Config.Set("user.email", "test@example.com");
            repo.Commit("seed", sig, sig);
        }

        // Two far-apart edits => two hunks.
        var modified = original.Replace("L2\n", "L2-changed\n").Replace("L11\n", "L11-changed\n");
        File.WriteAllText(file, modified);

        var fullPatch = service.GetFileDiff(_tempPath, "f.txt", isStaged: false);
        var firstHunk = FirstHunkOf(fullPatch);

        service.StageHunk(_tempPath, firstHunk);

        var stagedDiff = service.GetFileDiff(_tempPath, "f.txt", isStaged: true);
        var unstagedDiff = service.GetFileDiff(_tempPath, "f.txt", isStaged: false);

        Assert.Contains("L2-changed", stagedDiff);
        Assert.DoesNotContain("L11-changed", stagedDiff);
        Assert.Contains("L11-changed", unstagedDiff);
        Assert.DoesNotContain("L2-changed", unstagedDiff);
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
