using System;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Services;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the P2-07 EgressAllowlist view offscreen (headless Skia) in ALL FIVE themes so the
// design-system pass can be reviewed without a display. Two states: the default allowlist (no A6
// warning) and one with a user-added git-host entry (the A6 warning banner + row marker). PNGs land
// in the gitignored artifacts_headless/ — visual review, plus a non-empty-frame assertion.
public class EgressAllowlistRenderHarness
{
    [AvaloniaFact]
    public void Capture_EgressAllowlist_AllThemes()
    {
        try
        {
            foreach (var theme in ThemeManager.Themes)
            {
                ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                Capture(theme.Key, "default", new EgressAllowlistViewModel(new InMemoryEgressAllowlistGateway()));
                Capture(theme.Key, "a6_warning", BuildWithGitHost());
            }
        }
        finally
        {
            ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        }
    }

    private static EgressAllowlistViewModel BuildWithGitHost()
    {
        var gateway = new InMemoryEgressAllowlistGateway();
        gateway.Add("GitHub", "github.com", "GitHost");
        return new EgressAllowlistViewModel(gateway);
    }

    private static void Capture(string themeKey, string state, EgressAllowlistViewModel vm)
    {
        var win = new EgressAllowlistView { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"egress_allowlist_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"egress {themeKey}/{state} PNG is empty");

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
