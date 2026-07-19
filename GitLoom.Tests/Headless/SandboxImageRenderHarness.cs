using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Agents.Agents.Bootstrap;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the startup loading screen in its "installing sandbox images" state offscreen (headless
// Skia) in ALL FIVE THEMES. This is the visible surface for the sandbox-image version/staleness
// provisioning work (Item 1): after the daemon/updates stages finish, a MISSING or version-STALE jail
// image kicks a background docker-load/build and the loading screen shows the sandbox-images row
// Running with "Installing sandbox images in the background…" — the previously-discarded IProgress
// sink now also leaves per-step build/load breadcrumbs in oobe.log while it runs.
//
// The Tools → Rebuild sandbox images action and its "Rebuilding sandbox images…" / "Sandbox images
// updated." feedback reuse the EXISTING shell toast host + Tools-menu button styling (no new View,
// tokens, or classes), so they need no separate isolated render. Non-empty-frame assertions fail the
// build on a blank render; PNGs land in the gitignored artifacts_headless/. UI PENDING OWNER APPROVAL.
public class SandboxImageRenderHarness
{
    [AvaloniaFact]
    public void Capture_SandboxImagesInstalling_AllThemes()
    {
        try
        {
            foreach (var theme in GitLoom.App.Theming.ThemeManager.Themes)
            {
                GitLoom.App.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();
                CaptureStartup(theme.Key, "sandbox_images_installing", SandboxImagesInstalling());
            }
        }
        finally
        {
            GitLoom.App.Theming.ThemeManager.Apply(GitLoom.App.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    // Environment + daemon + updates done; the sandbox-images stage running with the background
    // install status line (the state AppStartupSequence enters after kicking a missing/stale build).
    private static StartupWindowViewModel SandboxImagesInstalling() => new(
        new[]
        {
            new BootstrapStageViewModel("Start the GitLoom OS environment", BootstrapStageState.Done),
            new BootstrapStageViewModel("Connect to the GitLoom OS daemon", BootstrapStageState.Done),
            new BootstrapStageViewModel("Apply updates", BootstrapStageState.Done),
            new BootstrapStageViewModel("Check sandbox images", BootstrapStageState.Running),
        },
        StartupStatus.InstallingImages);

    private static void CaptureStartup(string themeKey, string state, StartupWindowViewModel vm)
    {
        var win = new StartupWindow { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"startup_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"startup {themeKey}/{state} PNG is empty");

        HarnessHygiene.Teardown(win);
        Settle();
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(30);
        }
    }

    private static string ArtifactsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
