using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the partial-staging diff viewer (T-06 UI) offscreen so its hunk headers, per-hunk
// Stage/Discard buttons, selectable lines, and the selection action bar can be inspected.
public class PartialStagingRenderHarness
{
    [AvaloniaFact]
    public void Capture_PartialStaging()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var original = string.Join("\n", Enumerable.Range(1, 14).Select(n => $"line {n}")) + "\n";
        fx.CommitFile("Sample.cs", original, "seed");
        var modified = original.Replace("line 2\n", "line 2 CHANGED\n").Replace("line 12\n", "line 12 CHANGED\n");
        fx.WriteFile("Sample.cs", modified);

        var vm = new DiffViewerViewModel(git, fx.RepoPath);
        var view = new DiffViewerView { DataContext = vm };
        var win = new Window { Content = view, Width = 900, Height = 600 };
        win.Show();

        vm.UpdateDiff(new GitFileStatus { FilePath = "Sample.cs", State = FileStatus.ModifiedInWorkdir });
        Settle();

        Assert.True(vm.Hunks.Count >= 2);              // two well-separated edits -> two hunks
        Assert.False(vm.IsStagedView);                 // unstaged view -> Stage/Discard actions
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "partial_staging.png"));

        // Select the change lines in the first hunk -> the action bar appears.
        foreach (var line in vm.Hunks[0].Lines.Where(l => l.IsChange))
            line.IsSelected = true;
        Settle();

        Assert.True(vm.HasSelectedLines);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "partial_staging_selected.png"));
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
