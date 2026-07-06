using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-18 command palette offscreen (headless Skia) so its overlay/layout/highlighting can be
// inspected without a display. Captures PNGs to artifacts_headless/ — visual review, not pass/fail.
public class CommandPaletteRenderHarness
{
    private static PaletteEntry E(string title, string category, string gesture) =>
        new(title, category, gesture, () => Task.CompletedTask);

    private static IReadOnlyList<PaletteEntry> SampleEntries() => new[]
    {
        E("Commit", "Repository", "Ctrl+Enter"),
        E("Push", "Repository", "Ctrl+Shift+P"),
        E("Pull", "Repository", ""),
        E("Refresh Status", "Repository", "F5"),
        E("New Branch…", "Branch", "Ctrl+B"),
        E("Checkout feature/login", "Branch", ""),
        E("Toggle Sidebar", "View", ""),
        E("Open Command Palette", "General", "Ctrl+P"),
    };

    [AvaloniaFact]
    public void Capture_Palette_FilteredWithHighlights()
    {
        var vm = new CommandPaletteViewModel(SampleEntries);
        vm.Reset();
        vm.Query = "ch"; // matches "Checkout…" and "New Branch…" — highlighted match spans

        var win = HostWindow(vm, out var view);
        win.Show();
        Settle();
        view.FocusInput();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "command_palette_filtered.png"));

        Assert.Single(vm.Results, r => !r.IsHeader && string.Concat(SegmentsText(r)) == "Checkout feature/login");
        Assert.False(vm.HasNoResults);
    }

    [AvaloniaFact]
    public void Capture_Palette_BrowseGroupedByCategory()
    {
        var vm = new CommandPaletteViewModel(SampleEntries);
        vm.Reset(); // empty query → grouped browse mode

        var win = HostWindow(vm, out _);
        win.Show();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "command_palette_browse.png"));

        Assert.Contains(vm.Results, r => r.IsHeader);
    }

    [AvaloniaFact]
    public void Capture_Palette_TypedViaKeyInput()
    {
        // Drive the palette the way a user would: focus the box and type, so the Query binding is exercised.
        var vm = new CommandPaletteViewModel(SampleEntries);
        vm.Reset();

        var win = HostWindow(vm, out var view);
        win.Show();
        Settle();
        view.FocusInput();
        Settle();
        win.KeyTextInput("push");
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "command_palette_typed.png"));

        // The keystrokes flowed into the Query and filtered to "Push".
        Assert.Equal("push", vm.Query);
        Assert.Single(vm.Results, r => !r.IsHeader);
    }

    private static IEnumerable<string> SegmentsText(PaletteRowViewModel row)
    {
        foreach (var s in row.Segments) yield return s.Text;
    }

    private static Window HostWindow(CommandPaletteViewModel vm, out CommandPaletteView view)
    {
        view = new CommandPaletteView { DataContext = vm };
        // A scrim behind the card mirrors the real overlay so the PNG reads like the shipped UI.
        var scrim = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#C0000000")),
            Child = view,
        };
        return new Window { Width = 820, Height = 560, Content = scrim, SystemDecorations = SystemDecorations.None };
    }

    private static void Settle()
    {
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
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
