using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using Mainguard.Git.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests;

// TI-11 (ViewModel tier): the blame gutter must never render a stale result when the user
// switches files faster than blame can compute. The fake holds the first file's blame open on a
// gate; once a second file is requested and completes, releasing the first must NOT apply its
// (now stale) result.
public class BlameViewModelTests
{
    [Fact]
    public async Task BlameGutter_ShouldCancelStaleLoad_OnFileSwitch()
    {
        var resultA = new List<BlameLine> { new() { LineNumber = 1, Sha = "aaaaaaaa", ShortSha = "aaaaaaaa" } };
        var resultB = new List<BlameLine> { new() { LineNumber = 1, Sha = "bbbbbbbb", ShortSha = "bbbbbbbb" } };

        var aEntered = new ManualResetEventSlim(false);
        var aGate = new ManualResetEventSlim(false);

        var fake = new FakeGitService
        {
            GetBlameImpl = (_, path, _) =>
            {
                if (path == "a.txt")
                {
                    aEntered.Set();
                    aGate.Wait();       // hold the first (soon-to-be-stale) computation open
                    return resultA;
                }
                return resultB;         // the second file resolves immediately
            }
        };

        var applied = new List<string>();
        var vm = new BlameViewModel(fake, "/repo") { IsBlameVisible = true };
        vm.BlameApplied += p => { lock (applied) applied.Add(p); };

        // Start loading a.txt and wait until GetBlame has actually entered (and is now blocked).
        var loadA = vm.LoadAsync("a.txt");
        Assert.True(aEntered.Wait(TimeSpan.FromSeconds(5)));

        // Switch to b.txt before a.txt finishes — this cancels a.txt's load.
        await vm.LoadAsync("b.txt");

        // b.txt is what the gutter shows now.
        Assert.Equal("b.txt", vm.FilePath);
        Assert.Same(resultB, vm.BlameLines);
        lock (applied) Assert.Equal(new[] { "b.txt" }, applied.ToArray());

        // Now let a.txt finish. Its result is stale and must be discarded.
        aGate.Set();
        await loadA;

        Assert.Equal("b.txt", vm.FilePath);
        Assert.Same(resultB, vm.BlameLines);            // still b.txt's blame
        lock (applied) Assert.DoesNotContain("a.txt", applied);  // stale load never rendered
    }

    [Fact]
    public async Task ToggleBlame_Off_ShouldClearRows()
    {
        var result = new List<BlameLine> { new() { LineNumber = 1, Sha = "cccccccc" } };
        var fake = new FakeGitService { GetBlameImpl = (_, _, _) => result };

        var vm = new BlameViewModel(fake, "/repo");
        await vm.SetFileAsync("f.txt");

        await vm.ToggleBlameCommand.ExecuteAsync(null);   // on
        Assert.True(vm.IsBlameVisible);
        Assert.Same(result, vm.BlameLines);

        await vm.ToggleBlameCommand.ExecuteAsync(null);   // off
        Assert.False(vm.IsBlameVisible);
        Assert.Empty(vm.BlameLines);
    }

    [Fact]
    public async Task LoadAsync_ShouldSurfaceTypedError_WithoutThrowing()
    {
        var fake = new FakeGitService
        {
            GetBlameImpl = (_, _, _) => throw new Mainguard.Git.Exceptions.GitOperationException("nope.txt is gone"),
        };
        var vm = new BlameViewModel(fake, "/repo") { IsBlameVisible = true };

        await vm.LoadAsync("nope.txt");

        Assert.Equal("nope.txt is gone", vm.ErrorMessage);
        Assert.Empty(vm.BlameLines);
        Assert.False(vm.IsLoading);
    }
}
