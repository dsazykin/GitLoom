using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-12 file-history dialog offscreen (headless Skia): the rename-following revision list
// on the left and the selected revision's diff (vs its predecessor) on the right, against a real
// fixture repo. PNG to artifacts_headless/ for the human visual pass — proves the list rows AND the
// diff are actually visible (the dispatcher is pumped until history + diff have loaded, so the frame
// is never captured empty).
public class FileHistoryRenderHarness
{
    [AvaloniaFact]
    public void Capture_FileHistory()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var t0 = new DateTimeOffset(2024, 3, 1, 9, 0, 0, TimeSpan.Zero);

        // Four revisions of one file with distinct authors/dates so the list is legible and the
        // newest revision's diff has both additions and deletions.
        var v1 = "public sealed class Widget\n{\n    public int Size;\n}\n";
        fx.CommitFile("Widget.cs", v1, "add Widget", "Ada Lovelace", "ada@gitloom.local", t0);

        var v2 = v1.Replace("public int Size;", "public int Size;\n    public string Name = \"\";");
        fx.CommitFile("Widget.cs", v2, "add Name field", "Grace Hopper", "grace@gitloom.local", t0.AddDays(3));

        var v3 = v2.Replace("public int Size;", "public int Width;\n    public int Height;");
        fx.CommitFile("Widget.cs", v3, "split Size into Width/Height", "Linus Torvalds", "linus@gitloom.local", t0.AddDays(9));

        var v4 = v3.Replace("public string Name = \"\";", "public string Name { get; set; } = \"widget\";");
        fx.CommitFile("Widget.cs", v4, "make Name a property", "Margaret Hamilton", "peggy@gitloom.local", t0.AddDays(14));

        var vm = new FileHistoryViewModel(git, fx.RepoPath, "Widget.cs");
        var win = new FileHistoryView { DataContext = vm, Width = 1040, Height = 660 };
        win.Show();

        // OnOpened kicks off LoadAsync; drive it explicitly too so the harness is deterministic.
        _ = vm.LoadAsync();
        Pump(() => vm.Versions.Count == 4 && vm.DiffLines.Count > 0 && !vm.IsLoading);
        Settle();

        // History is wired end-to-end: four revisions, newest-first, newest auto-selected with a diff.
        Assert.Equal(4, vm.Versions.Count);
        Assert.Equal("make Name a property", vm.Versions[0].MessageShort);
        Assert.Equal(vm.Versions[0].Sha, vm.SelectedVersion!.Sha);
        Assert.True(vm.HasDiffLines);
        Assert.Contains(vm.DiffLines, l => l.IsAdded);
        Assert.Contains(vm.DiffLines, l => l.IsRemoved);

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "file_history.png"));

        // Also capture the introducing revision (all-additions render) for the visual pass.
        vm.SelectedVersion = vm.Versions.Last();
        Pump(() => vm.DiffLines.Any(l => l.IsAdded) && vm.DiffLines.All(l => !l.IsRemoved));
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "file_history_introduction.png"));
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static void Pump(Func<bool> until)
    {
        for (int i = 0; i < 250 && !until(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
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
