using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Xunit;

namespace GitLoom.Tests.Headless;

// P2-22 §J-5: renders the "Agent CLIs" settings window (the add-more-later surface over the pinned
// starter channel) offscreen in ALL FIVE THEMES across every state it can show — the normal list
// (installed + not-installed + a long-name truncation row), the mid-install state (per-row progress,
// Cancel install, serialized rows), a per-row failure with its actionable cause, the loading line,
// and the catalog-read error card — so the design-system pass (tokens, component classes, icon+text
// state encoding, light Daylight Loom) can be reviewed without a display. PNGs land in the
// gitignored artifacts_headless/.
public class AgentCliSettingsRenderHarness
{
    [AvaloniaFact]
    public void Capture_AgentCliSettings_AllThemes()
    {
        try
        {
            foreach (var theme in GitLoom.App.Theming.ThemeManager.Themes)
            {
                GitLoom.App.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                Capture(theme.Key, "list", new AgentCliSettingsViewModel(ListMix()));
                Capture(theme.Key, "installing", InstallingVm());
                Capture(theme.Key, "failure", new AgentCliSettingsViewModel(FailureMix()));
                Capture(theme.Key, "loading", new AgentCliSettingsViewModel(
                    Array.Empty<AgentCliRowViewModel>(), isLoading: true));
                Capture(theme.Key, "load_error", new AgentCliSettingsViewModel(
                    Array.Empty<AgentCliRowViewModel>(),
                    loadError: "GitLoom could not read its agent-CLI catalog: the GitLoom environment "
                        + "did not answer (is it still starting?). If the GitLoom environment is not "
                        + "running, open GitLoom again to start it, then Refresh."));
            }
        }
        finally
        {
            GitLoom.App.Theming.ThemeManager.Apply(GitLoom.App.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    private static IEnumerable<AgentCliRowViewModel> ListMix() => new[]
    {
        new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
        new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4"),
        new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
        new AgentCliRowViewModel("long", "An Agent CLI With A Deliberately Very Long Product Name That Truncates", "10.20.300-rc.4+build.99"),
    };

    private static AgentCliSettingsViewModel InstallingVm()
    {
        var rows = new[]
        {
            new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
            new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4")
            {
                IsInstalling = true,
                StatusMessage = "Downloading, verifying, and installing — this can take a few minutes "
                    + "on a slow connection.",
            },
            new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
        };
        var vm = new AgentCliSettingsViewModel(rows) { IsBusy = true };
        return vm;
    }

    private static IEnumerable<AgentCliRowViewModel> FailureMix() => new[]
    {
        new AgentCliRowViewModel("claude-code", "Claude Code", "2.1.210", isInstalled: true),
        new AgentCliRowViewModel("codex", "OpenAI Codex CLI", "0.144.4")
        {
            IsFailed = true,
            StatusMessage = "codex could not be installed inside the GitLoom VM: npm exited 243 "
                + "(network unreachable). You can try again from Settings once setup finishes; "
                + "GitLoom works without it.",
        },
        new AgentCliRowViewModel("opencode", "OpenCode", "1.18.1"),
    };

    private void Capture(string themeKey, string state, AgentCliSettingsViewModel vm)
    {
        var win = new AgentCliSettingsView { DataContext = vm };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"agent_cli_settings_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"agent cli settings {themeKey}/{state} PNG is empty");

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
