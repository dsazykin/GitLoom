using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.Controls;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.Controls;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.Controls;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-09 commit graph with a context menu open (headless Skia) and drives the pure
// GraphHitTester through the real CommitGraphCanvas on a right-click coordinate, proving the
// hit-test → menu-construction path end to end. Captures a PNG to artifacts_headless/ for review.
public class GraphInteractionsRenderHarness
{
    [AvaloniaFact]
    public void Capture_CommitGraph_WithContextMenuOpen()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "1\n", "initial commit");
        fx.CommitFile("b.txt", "2\n", "add feature b");
        var top = fx.CommitFile("c.txt", "3\n", "fix bug in c");
        fx.CreateBranch("feature/x");

        var vm = new CommitTimelineViewModel(git, fx.RepoPath);
        vm.LoadInitialCommits();

        var view = new CommitTimelineView { DataContext = vm };
        var win = new Window { Width = 1100, Height = 640, Content = view };
        win.Show();
        Settle();

        // Drive right-click hit-testing on the newest row's graph canvas: the dot for lane 0
        // sits at x = laneSpacing/2 (7.5), vertically centered.
        var canvas = view.GetVisualDescendants().OfType<CommitGraphCanvas>().FirstOrDefault();
        Assert.NotNull(canvas);
        var hit = canvas!.HitTest(new Point(7.5, canvas.Bounds.Height / 2));

        Assert.Equal(GraphHitKind.Node, hit.Kind);
        Assert.Equal(top, hit.Sha);

        // Build the menu the ViewModel would show for that hit and open it over the canvas so the
        // captured frame includes the menu (headless overlay popup).
        var items = vm.BuildContextMenuForHit(hit);
        Assert.NotNull(items);
        var menu = BuildContextMenu(items!);
        menu.Open(canvas);
        Settle();

        var path = Path.Combine(ArtifactsDir(), "graph_interactions_menu.png");
        win.CaptureRenderedFrame()?.Save(path);

        Assert.True(File.Exists(path));
        HarnessHygiene.Teardown(win);
    }

    private static ContextMenu BuildContextMenu(ObservableCollection<MenuItemViewModel> items)
    {
        var menu = new ContextMenu();
        foreach (var item in items)
        {
            menu.Items.Add(BuildMenuNode(item));
        }
        return menu;
    }

    private static Control BuildMenuNode(MenuItemViewModel vm)
    {
        if (vm is SeparatorViewModel) return new Separator();
        var mi = new MenuItem { Header = vm.Header, Command = vm.Command, CommandParameter = vm.CommandParameter, IsEnabled = vm.IsEnabled };
        foreach (var child in vm.SubItems) mi.Items.Add(BuildMenuNode(child));
        return mi;
    }

    private static void Settle()
    {
        for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
