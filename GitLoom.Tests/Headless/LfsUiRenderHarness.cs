using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-17 Git LFS panel offscreen (headless Skia) so its layout / theme / status chips can
// be inspected without a display. Canned data via FakeLfsService → deterministic, no git.
// Captures PNGs to artifacts_headless/ — visual review, not pass/fail.
public class LfsUiRenderHarness
{
    [AvaloniaFact]
    public void Capture_LfsWindow_Populated()
    {
        var fake = new FakeLfsService
        {
            IsAvailableImpl = _ => true,
            IsEnabledForRepoImpl = _ => true,
            ListTrackedPatternsImpl = _ => new[] { "*.psd", "*.bin", "assets/*.zip" },
            ListLfsFilesImpl = _ => new List<LfsFile>
            {
                new() { Oid = "394e150401779536293e71470142d31b9af32750fb50c9c548d63632cf512d40", Path = "art/hero.psd", IsDownloaded = true },
                new() { Oid = "f4bae3678fa9268d2be6dac4a6a023f744b713d343778200a61d4cfa1d6dbcb7", Path = "art/my file.bin", IsDownloaded = true },
                new() { Oid = "aa11bb22cc33dd44ee55ff6600112233445566778899aabbccddeeff00112233", Path = "assets/pack.zip", IsDownloaded = false },
            }
        };

        var vm = new LfsViewModel(fake, "/tmp/repo");
        var win = new LfsWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "lfs_window.png"));

        Assert.True(vm.IsAvailable);
        Assert.Equal(3, vm.Patterns.Count);
        Assert.Equal(3, vm.Files.Count);
        Assert.False(vm.HasNoFiles);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_LfsWindow_NotInstalled()
    {
        var fake = new FakeLfsService { IsAvailableImpl = _ => false };

        var vm = new LfsViewModel(fake, "/tmp/repo");
        var win = new LfsWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "lfs_window_not_installed.png"));

        Assert.False(vm.IsAvailable);
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
