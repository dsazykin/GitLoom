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
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// In-depth daemon logging (§E): renders the read-only "Daemon logs" settings window offscreen in ALL
// FIVE THEMES across the states it can show — populated (a realistic mixed-subsystem journal tail), the
// loading line, and the honest empty state (VM down / no such log yet) — so the design-system pass
// (tokens, role classes, the SurfaceDeep mono log pane, light Daylight Loom) can be reviewed without a
// display. PNGs land in the gitignored artifacts_headless/. UI is PENDING USER APPROVAL — these frames
// are the review artifact, not a sign-off.
public class DaemonLogsRenderHarness
{
    // A representative, secret-safe journal tail — the file line format across subsystems, including a
    // correlated spawn scope (a7f3c2) and a masked egress denial. No real secret ever appears.
    private const string SampleJournal =
        "2026-07-18T09:12:03.1120000+00:00 [INF] [lifecycle] () options parsed: port=5250 localDev=False smoke=False\n"
        + "2026-07-18T09:12:03.4550000+00:00 [INF] [migration] () preparing db path=/home/mainguard/.mainguard/mainguard-daemon.db\n"
        + "2026-07-18T09:12:03.4610000+00:00 [INF] [migration] () stale migration lock cleared\n"
        + "2026-07-18T09:12:03.9880000+00:00 [INF] [migration] () migrate ok (512ms)\n"
        + "2026-07-18T09:12:04.0020000+00:00 [INF] [lifecycle] () bound 127.0.0.1:5250 — daemon ready\n"
        + "2026-07-18T09:14:20.3300000+00:00 [INF] [spawn] (a7f3c2) spawn: session created role=coordinator kind=claude-code\n"
        + "2026-07-18T09:14:20.9910000+00:00 [INF] [spawn] (a7f3c2) preflight ok: jail images present\n"
        + "2026-07-18T09:14:21.0450000+00:00 [INF] [spawn] (a7f3c2) egress ready (default-deny network + proxy)\n"
        + "2026-07-18T09:14:21.4400000+00:00 [INF] [spawn] (a7f3c2) jail started: container=9b2f1c reused=False launchCmd=True\n"
        + "2026-07-18T09:14:21.4600000+00:00 [INF] [rpc] () rpc-end method=/mainguard.v1.AgentService/SpawnAgent status=OK duration_ms=1131 request=model_api_key=*** kind=claude-code\n"
        + "2026-07-18T09:15:02.7710000+00:00 [WRN] [egress] () egress Denied host=api.github.com kind=connect agent=a7f3c2 bytes=0\n";

    [AvaloniaFact]
    public void Capture_DaemonLogs_AllThemes()
    {
        try
        {
            foreach (var theme in Mainguard.UI.Theming.ThemeManager.Themes)
            {
                Mainguard.UI.Theming.ThemeManager.Apply(theme.Key, persist: false);
                Settle();

                Capture(theme.Key, "populated", new DaemonLogsViewModel(SampleJournal, selectedSource: DaemonLogsViewModel.JournalLabel));
                Capture(theme.Key, "spawn", new DaemonLogsViewModel(SampleJournal, selectedSource: "spawn"));
                Capture(theme.Key, "loading", new DaemonLogsViewModel(logText: null, isLoading: true));
                Capture(theme.Key, "empty", new DaemonLogsViewModel(logText: null));
            }
        }
        finally
        {
            Mainguard.UI.Theming.ThemeManager.Apply(Mainguard.UI.Theming.ThemeManager.DefaultKey, persist: false);
        }
    }

    private void Capture(string themeKey, string state, DaemonLogsViewModel vm)
    {
        // DaemonLogsView is a UserControl now (embedded as a Settings page) — wrap it in a plain
        // Window for the headless render harness, same as every other migrated page harness.
        var win = new Avalonia.Controls.Window
        {
            Width = 760,
            Height = 620,
            Content = new DaemonLogsView { DataContext = vm },
        };
        win.Show();
        Settle();

        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var path = Path.Combine(ArtifactsDir(), $"daemon_logs_{themeKey}_{state}.png");
        frame!.Save(path);
        Assert.True(new FileInfo(path).Length > 0, $"daemon logs {themeKey}/{state} PNG is empty");

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
        while (dir != null && !File.Exists(Path.Combine(dir, "Mainguard.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
