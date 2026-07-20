using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
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

// Renders the T-10 remotes UI offscreen (headless Skia) so its layout/theme can be
// inspected without a display. Captures PNGs to artifacts_headless/ — visual review,
// not pass/fail.
public class RemotesUiRenderHarness
{
    [AvaloniaFact]
    public void Capture_RemotesWindow_WithRemotes()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "hello\n", "init");
        git.AddRemote(fx.RepoPath, "origin", "https://github.com/dsazykin/Mainguard.git");
        git.AddRemote(fx.RepoPath, "upstream", "git@github.com:acme/mainguard.git");

        var vm = new RemotesViewModel(git, fx.RepoPath) { NewRemoteName = "backup", NewRemoteUrl = "https://example.com/backup.git" };
        var win = new RemotesWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "remotes_window.png"));

        Assert.Equal(2, vm.Remotes.Count);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_RemotesWindow_Empty()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        fx.CommitFile("a.txt", "hello\n", "init");

        var vm = new RemotesViewModel(git, fx.RepoPath);
        var win = new RemotesWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "remotes_window_empty.png"));

        Assert.Empty(vm.Remotes);
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
