using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Models;
using GitLoom.Tests.Fixtures;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the real MainWindow shell with a repository open, so the top-nav toolbar — the branch
// dropdown plus the grouped Sync / Collaborate / Tools menus (#67) — and the opening overlay (#63)
// / auto-closed sidebar (#61) can be inspected. Visual review, not pass/fail.
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
            win.Close();
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

            var vm = new SettingsViewModel(GitLoom.App.App.Settings, onPinsChanged: () => { });
            var win = new SettingsWindow { DataContext = vm };
            win.Show();
            for (int i = 0; i < 30; i++) Pump();

            Assert.NotEmpty(vm.PinRows);

            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "settings_window.png"));
            win.Close();
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
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
