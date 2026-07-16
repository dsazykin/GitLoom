using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Models;
using GitLoom.Core.Services;
using GitLoom.Tests.Fixtures;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests.Headless;

// Regression harness for GitHub #82: the app crashed when a selected/staged file was renamed on
// disk. A RepositoryWatcher refresh then re-selects/reloads the diff and clears the editor content
// while AvaloniaEdit's LineNumberMargin may still hold VisualLines referencing the old (now-deleted)
// DocumentLines — the render dereferenced a deleted line (DocumentLine.LineNumber threw) and took
// down the render pipeline.
//
// This drives the real DiffViewerView + DiffViewerViewModel in the Code-Editor (line-numbers on)
// mode: loads a file into the editor, renders a frame, then simulates the watcher refresh finding
// the file gone (UpdateDiff(null)), pumps the dispatcher and forces another render, and asserts no
// exception + the editor safely clears. Headless render is synchronous so it cannot perfectly
// reproduce the Win32 compositor race, but it locks in the safe-clear-on-file-gone path and would
// have thrown on the old in-place Document.Text mutation when a stale line survived.
public class DiffViewerFileRemovedRenderHarness
{
    [AvaloniaFact]
    public void FileRenamedOnDisk_RefreshClearsEditor_NoCrash()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();

        // A committed multi-line file, then an on-disk edit so there is a real diff and the editor
        // shows several numbered lines (LineNumberMargin has work to do).
        var original = string.Join("\n", new[] { "one", "two", "three", "four", "five" }) + "\n";
        fx.CommitFile("notes.txt", original, "seed notes");
        var full = Path.Combine(fx.RepoPath, "notes.txt");
        File.WriteAllText(full, original.Replace("three", "THREE CHANGED").Replace("five", "FIVE CHANGED"));

        var vm = new DiffViewerViewModel(git, fx.RepoPath) { IsEditMode = true };
        var view = new DiffViewerView { DataContext = vm };
        var win = new Window { Content = view, Width = 700, Height = 400 };
        win.Show();

        // Select the file → editor loads its text with line numbers.
        var status = new GitFileStatus { FilePath = "notes.txt", State = FileStatus.ModifiedInWorkdir };
        vm.UpdateDiff(status);
        Settle();
        RenderFrame(win);

        var editor = FindEditor(view);
        Assert.NotNull(editor);
        Assert.Contains("THREE CHANGED", editor!.Document?.Text ?? "");

        // Simulate the OS rename: the working file disappears, and the watcher-driven refresh finds
        // the selection is gone → UpdateDiff(null). Pump + force a render; the old code crashed here.
        File.Move(full, Path.Combine(fx.RepoPath, "notes-renamed.txt"));
        var ex = Record.Exception(() =>
        {
            vm.UpdateDiff(null);
            Settle();
            RenderFrame(win);

            // Hammer the swap a few times (content ↔ empty) while rendering, to stress the
            // document-replace path the way a burst of watcher events would.
            for (int i = 0; i < 5; i++)
            {
                vm.UpdateDiff(i % 2 == 0 ? status : null);
                Settle();
                RenderFrame(win);
            }
            vm.UpdateDiff(null);
            Settle();
            RenderFrame(win);
        });

        Assert.Null(ex);
        Assert.Equal(string.Empty, vm.RawContent);
        Assert.False(vm.HasFile);
        Assert.Equal(string.Empty, editor!.Document?.Text ?? "");

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "diffviewer_file_removed_cleared.png"));
        HarnessHygiene.Teardown(win);
    }

    private static TextEditor? FindEditor(Control root)
    {
        foreach (var d in root.GetVisualDescendants())
            if (d is TextEditor te) return te;
        return null;
    }

    private static void RenderFrame(Window win)
    {
        // CaptureRenderedFrame drives a full render pass (LineNumberMargin.Render included).
        win.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Settle()
    {
        for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
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
