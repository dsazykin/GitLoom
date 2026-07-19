using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.Theming;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Agents.Agents;
using Mainguard.Agents.Agents.Mock;
using Xunit;

namespace GitLoom.Tests.Headless;

// Lane E Part 3 (revised 2026-07-11): renders the coordinator surface — the control-center
// content MainWindow hosts behind the section rail — offscreen with its four scripted agents
// in EVERY theme (never assume dark), in both workspace layouts (Flight Deck / Conversation
// Deck; The Loom is retired), after the stale cascade, while frozen, and the Vibe surface
// (headed for its own app; captured here so it stays alive). PNGs go to artifacts_headless/;
// the load-bearing VM truths (gate reasons, cascade, freeze) are asserted along the way.
public class ControlCenterRenderHarness
{
    private static readonly string[] ThemeKeys = { "MidnightLoom", "DaylightLoom", "CommandDeck", "Atelier", "LoomAurora" };

    [AvaloniaFact]
    public void Capture_CoordinatorSurface_AllFiveThemes()
    {
        foreach (var theme in ThemeKeys)
        {
            ThemeManager.Apply(theme, persist: false);
            using var vm = NewVm(out _);
            vm.SelectAgent("loom-3"); // agent focus: document + review gate + queue rail
            var win = HostWindow(new CoordinatorSurfaceView { DataContext = vm });
            win.Show();
            Settle();

            Assert.Equal(4, vm.Agents.Count);
            Assert.True(vm.Queue.Entries.Count >= 4);
            win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), $"coordinator_surface_{theme}.png"));
            HarnessHygiene.Teardown(win);
        }
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
    }

    [AvaloniaFact]
    public void Capture_Layouts_FlightDeck_And_ConversationDeck()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm(out _);
        var win = HostWindow(new CoordinatorSurfaceView { DataContext = vm });
        win.Show();

        vm.FocusCoordinator(); // Flight Deck: the conversation is the center content
        Settle();
        Assert.True(vm.IsFlightDeck);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "coordinator_flightdeck_chat.png"));

        vm.SetPreset("ConversationDeck"); // chat pins left; center shows telemetry
        Settle();
        Assert.True(vm.IsConversationDeck);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "coordinator_conversationdeck.png"));

        vm.SetPreset("Loom"); // retired preset: falls back to Flight Deck, never a blank shell
        Assert.True(vm.IsFlightDeck);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_MergeGate_And_StaleCascade()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm(out var mock);
        var win = HostWindow(new CoordinatorSurfaceView { DataContext = vm });
        win.Show();
        Settle();

        // The gate holds while flagged items are unacknowledged (P2-11), wording per §3.4.
        Assert.False(mock.CanMerge("loom-3", out var reason));
        Assert.Contains("flagged", reason);

        vm.SelectAgent("loom-3");
        Settle();
        foreach (var item in vm.SelectedDocument!.FlaggedItems.ToList())
            item.AcknowledgeCommand.Execute(null);
        Settle();
        Assert.True(vm.SelectedDocument.CanMerge);

        vm.SelectedDocument.MergeCommand.Execute(null);
        Settle();

        // The stale cascade: no other Verified/AwaitingReview entry survives fresh (P2-10 step 3).
        Assert.DoesNotContain(mock.GetQueue(), q =>
            q.AgentId != "loom-3" && q.State is WorkerMergeState.Verified or WorkerMergeState.AwaitingReview);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "coordinator_stale_cascade.png"));
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_KillSwitch_Frozen()
    {
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm(out var mock);
        var win = HostWindow(new CoordinatorSurfaceView { DataContext = vm });
        win.Show();
        Settle();

        vm.ToggleKillSwitchCommand.Execute(null);
        Settle();
        Assert.True(vm.IsFrozen);
        Assert.False(mock.CanMerge("loom-3", out var reason));
        Assert.Contains("frozen", reason);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "coordinator_frozen.png"));
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_VibeSurface_Chat_And_Triage()
    {
        // Vibe is headed for its own app; the surface renders standalone here.
        ThemeManager.Apply(ThemeManager.DefaultKey, persist: false);
        using var vm = NewVm(out _);
        var win = HostWindow(new VibeModeView { DataContext = vm.Vibe });
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "vibe_chat.png"));

        // Triage (P3-02): three actions; "Go back" is enabled because a green checkpoint exists.
        vm.Vibe.SimulateSnagCommand.Execute(null);
        Settle();
        Assert.True(vm.Vibe.IsTriageVisible);
        Assert.True(vm.Vibe.CanGoBack);
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "vibe_triage.png"));

        vm.Vibe.ChooseGoBackCommand.Execute(null);
        Settle();
        Assert.False(vm.Vibe.IsTriageVisible);
        HarnessHygiene.Teardown(win);
    }

    private static Window HostWindow(Control content)
    {
        var win = new Window { Width = 1420, Height = 920, Content = content };
        // The surface normally sits on MainWindow's SurfaceWindow background; mirror it here
        // so captures show the real surface stepping under the current theme.
        if (Avalonia.Application.Current!.TryGetResource("SurfaceWindow", null, out var bg) && bg is Avalonia.Media.IBrush brush)
            win.Background = brush;
        return win;
    }

    private static ControlCenterViewModel NewVm(out MockOrchestrator mock)
    {
        // A slow tick keeps captures deterministic; transitions are driven via commands.
        mock = new MockOrchestrator(TimeSpan.FromHours(1));
        return new ControlCenterViewModel(mock);
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
