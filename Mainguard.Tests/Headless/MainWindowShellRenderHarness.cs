using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.Theming;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the real MainWindow shell with a repository open, so the top-nav toolbar — the branch
// dropdown plus the grouped Sync / Tools menus (#67; no Collaborate on phase2 — the host surfaces
// live in the section rail) — and the opening overlay (#63) can be inspected. Visual review,
// not pass/fail.
public class MainWindowShellRenderHarness
{
    [AvaloniaFact]
    public void Capture_MainWindow_TopNavAndShell()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo repo\n", "chore: seed");
        fx.CommitFile("src/app.cs", "class App { }\n", "feat: app");
        fx.CreateBranch("feature/work");

        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            win.Show();

            vm.OpenRepository(new Repository { Path = fx.RepoPath, DisplayName = "demo" });

            // Wait for the async open (Task.Run VM build) to land, then let the dashboard's initial
            // load settle so the toolbar and workspace are fully painted.
            for (int i = 0; i < 200 && vm.CurrentWorkspace == null; i++) Pump();
            for (int i = 0; i < 80; i++) Pump();

            Assert.NotNull(vm.CurrentWorkspace);

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "mainwindow_shell.png"));
            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    [AvaloniaFact]
    public void Capture_Toasts_Stacked()
    {
        using var fx = new TempRepoFixture();
        fx.CommitFile("readme.md", "# demo repo\n", "chore: seed");

        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            var vm = new MainWindowViewModel();
            var win = new MainWindow { DataContext = vm, Width = 1400, Height = 900 };
            win.Show();
            vm.OpenRepository(new Repository { Path = fx.RepoPath, DisplayName = "demo" });
            for (int i = 0; i < 200 && vm.CurrentWorkspace == null; i++) Pump();
            for (int i = 0; i < 40; i++) Pump();

            // Stack a few toasts (#85): a normal one, an error one, and a long one to show trimming.
            if (vm.CurrentWorkspace is RepoDashboardViewModel dash)
            {
                dash.ShowNotification("Fetched origin — 3 new commits.", isError: false);
                dash.ShowNotification("Push failed: remote rejected (non-fast-forward).", isError: true);
                dash.ShowNotification("Rebased feature/login onto main; resolved 2 conflicts and re-applied 5 commits successfully.", isError: false);
                for (int i = 0; i < 80; i++) Pump();
                Assert.Equal(3, dash.Toasts.Count);
            }

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "toasts_stacked.png"));
            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    [AvaloniaFact]
    public void Capture_SettingsWindow_PinnedMenuPicker()
    {
        try
        {
            ThemeManager.Apply("MidnightLoom", persist: false);

            var vm = new SettingsViewModel(Mainguard.App.Shell.App.Settings, onPinsChanged: () => { });
            var win = new SettingsWindow { DataContext = vm };
            win.Show();
            for (int i = 0; i < 30; i++) Pump();

            Assert.NotEmpty(vm.PinRows);

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "settings_window.png"));
            HarnessHygiene.Teardown(win);
        }
        finally
        {
            ThemeManager.Apply("MidnightLoom", persist: false);
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(25);
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
