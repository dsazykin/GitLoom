using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-19 operation-history window offscreen (headless Skia) so its layout/theme
// and the per-entry undo/redo affordances can be inspected without a display. Captures a
// PNG to artifacts_headless/ — visual review, not pass/fail.
public class OperationHistoryRenderHarness
{
    [AvaloniaFact]
    public void Capture_OperationHistoryWindow_WithEntries()
    {
        using var fx = new TempRepoFixture();
        var dbPath = Path.Combine(Path.GetTempPath(), "gitloom-journal-render-" + Guid.NewGuid().ToString("N") + ".db");
        using (var ctx = new AppDbContext(dbPath)) ctx.Database.Migrate();
        var journal = new OperationJournal(() => new AppDbContext(dbPath));
        var git = new GitService(null, journal);

        // Build a realistic history: several undoable ops, one undone, plus a non-undoable one.
        var c1 = fx.CommitFile("a.txt", "1\n", "initial");
        git.CreateBranch(fx.RepoPath, "feature", "", checkout: false);
        git.CreateTag(fx.RepoPath, "v1", c1, "release one");
        fx.WriteFile("a.txt", "2\n");
        git.StageFile(fx.RepoPath, "a.txt");
        git.Commit(fx.RepoPath, "second change");
        fx.WriteFile("a.txt", "3\n");
        git.StashPush(fx.RepoPath, "wip");
        git.StashDrop(fx.RepoPath, 0); // non-undoable, flagged

        // Undo the most recent undoable op so an "Undone" row is visible with a Redo button.
        var commitEntry = journal.GetHistory(fx.RepoPath).First(e => e.Kind == JournalKinds.CreateTag);
        journal.Undo(fx.RepoPath, commitEntry.Id);

        var vm = new OperationHistoryViewModel(journal, fx.RepoPath);
        var win = new OperationHistoryWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "operation_history_window.png"));

        Assert.True(vm.HasEntries);
        Assert.Contains(vm.Entries, e => e.IsUndone);       // redo affordance present
        Assert.Contains(vm.Entries, e => !e.IsUndoable);    // non-undoable flagged row present

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(dbPath); } catch { }
        HarnessHygiene.Teardown(win);
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
