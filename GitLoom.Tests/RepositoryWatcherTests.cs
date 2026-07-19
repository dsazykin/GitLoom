using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests;

// Backfill B-8 (test strategy doc): the positive refs-change trigger and the
// burst-coalescing behavior of fix 1.10 (the existing suite only proves the
// negative cases — lock files and ignored dirs).
public class RepositoryWatcherTests : IDisposable
{
    private readonly TempRepoFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task RepositoryWatcher_ShouldTrigger_OnRefsChange()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");

        var tcs = new TaskCompletionSource<bool>();
        using var watcher = new RepositoryWatcher(_fx.RepoPath, debounceMs: 100);
        watcher.RepositoryChanged += () => tcs.TrySetResult(true);

        // A ref update (what branch create/checkout/commit do under the hood).
        var refPath = Path.Combine(_fx.RepoPath, ".git", "refs", "heads", "watched-branch");
        await File.WriteAllTextAsync(refPath, new string('0', 40) + "\n");

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public async Task RepositoryWatcher_ShouldCoalesceBurst_IntoBoundedFires()
    {
        _fx.CommitFile("a.txt", "x\n", "seed");

        int fires = 0;
        using var watcher = new RepositoryWatcher(_fx.RepoPath, debounceMs: 100);
        watcher.RepositoryChanged += () => Interlocked.Increment(ref fires);

        // 50 rapid working-tree writes must not produce 50 refreshes: the
        // debounce + 250 ms rate cap bound the fire count.
        for (int i = 0; i < 50; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(_fx.RepoPath, "burst.txt"), $"write {i}\n");
        }

        // Wait past debounce + rate-cap windows for trailing fires to land.
        await Task.Delay(1500);

        Assert.InRange(fires, 1, 8);
    }
}
