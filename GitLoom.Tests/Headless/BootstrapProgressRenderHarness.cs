using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using GitLoom.Core.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the P2-05 staged-checklist bootstrap progress window (BootstrapProgressView) offscreen
// (headless Skia) in ALL FIVE themes, so the design-system pass — icon-per-state encoding, tokens,
// component classes, and the light Daylight Loom theme — can be reviewed without a display. Captures
// a mid-run mix (done / running / pending) and a failed run (with the error banner) to the gitignored
// artifacts_headless/. A non-empty-frame assertion fails the build on a blank render.
public class BootstrapProgressRenderHarness
{
    [AvaloniaFact]
    public void Capture_BootstrapProgress_AllThemes()
    {
        try
        {
            foreach (var theme in GitLoom.App.Theming.ThemeManager.Themes)
            {
                GitLoom.App.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                CaptureState(theme.Key, "running", BuildRunningMix());
                CaptureState(theme.Key, "failed", BuildFailed());
            }
        }
        finally
        {
            GitLoom.App.Theming.ThemeManager.Apply(GitLoom.App.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    // A mid-run checklist: earlier steps done, one running (with a log tail), the rest pending.
    private static BootstrapProgressViewModel BuildRunningMix() => new(new[]
    {
        new BootstrapStageViewModel("Detect WSL2", BootstrapStageState.Done),
        new BootstrapStageViewModel("Import GitLoomEnv", BootstrapStageState.Done, "GitLoomEnv imported."),
        new BootstrapStageViewModel("Configure WSL memory", BootstrapStageState.Done),
        new BootstrapStageViewModel("First boot (sysctls + Docker)", BootstrapStageState.Running,
            "Waiting for Docker to become ready…"),
        new BootstrapStageViewModel("Start gitloomd", BootstrapStageState.Pending),
        new BootstrapStageViewModel("Health-check daemon", BootstrapStageState.Pending),
    });

    // A failed run: a step failed, later steps never reached — plus the error banner.
    private static BootstrapProgressViewModel BuildFailed()
    {
        var vm = new BootstrapProgressViewModel(new[]
        {
            new BootstrapStageViewModel("Detect WSL2", BootstrapStageState.Done),
            new BootstrapStageViewModel("Import GitLoomEnv", BootstrapStageState.Done),
            new BootstrapStageViewModel("Configure WSL memory", BootstrapStageState.Done),
            new BootstrapStageViewModel("First boot (sysctls + Docker)", BootstrapStageState.Failed,
                "Docker did not become ready inside GitLoomEnv."),
            new BootstrapStageViewModel("Start gitloomd", BootstrapStageState.Pending),
            new BootstrapStageViewModel("Health-check daemon", BootstrapStageState.Pending),
        })
        {
            ErrorMessage = "Docker did not become ready inside GitLoomEnv. Check the dockerd logs and retry.",
        };
        return vm;
    }

    private void CaptureState(string themeKey, string state, BootstrapProgressViewModel vm)
    {
        var win = new BootstrapProgressView { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"bootstrap_progress_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"bootstrap {themeKey}/{state} PNG is empty");

        HarnessHygiene.Teardown(win);
        Settle();
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
