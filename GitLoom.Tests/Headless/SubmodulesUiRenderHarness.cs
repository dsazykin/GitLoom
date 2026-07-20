using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Git.Models;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-16 submodules UI offscreen (headless Skia) so its layout/theme/status chips can
// be inspected without a display. Canned list via FakeGitService → deterministic, no git.
// Captures PNGs to artifacts_headless/ — visual review, not pass/fail.
public class SubmodulesUiRenderHarness
{
    [AvaloniaFact]
    public void Capture_SubmodulesWindow_Populated()
    {
        var fake = new FakeGitService
        {
            GetSubmodulesImpl = _ => new List<SubmoduleItem>
            {
                new() { Path = "vendor/libfoo", Url = "https://github.com/acme/libfoo.git", HeadSha = "a1b2c3d4e5f6", Status = SubmoduleState.UpToDate },
                new() { Path = "vendor/lib bar", Url = "https://github.com/acme/libbar.git", HeadSha = "0f1e2d3c4b5a", Status = SubmoduleState.Modified },
                new() { Path = "third_party/baz", Url = "git@github.com:acme/baz.git", HeadSha = "9988776655ab", Status = SubmoduleState.Dirty },
                new() { Path = "third_party/qux", Url = "https://example.com/qux.git", HeadSha = null, Status = SubmoduleState.Uninitialized },
            }
        };

        var vm = new SubmodulesViewModel(fake, "/tmp/super");
        var win = new SubmodulesWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "submodules_window.png"));

        Assert.Equal(4, vm.Submodules.Count);
        Assert.False(vm.IsEmpty);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_SubmodulesWindow_Empty()
    {
        var fake = new FakeGitService { GetSubmodulesImpl = _ => new List<SubmoduleItem>() };

        var vm = new SubmodulesViewModel(fake, "/tmp/super");
        var win = new SubmodulesWindow { DataContext = vm };
        win.Show();

        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "submodules_window_empty.png"));

        Assert.Empty(vm.Submodules);
        Assert.True(vm.IsEmpty);
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
