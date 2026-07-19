using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Git.Safety;
using Mainguard.Git.Services;
using Mainguard.Tests.Fakes;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-31 conventional-commit composer offscreen (headless Skia): the filled structured
// composer (type/scope/description with the amber over-limit counter, body, breaking, co-author + issue
// chips, live preview, and a validation warning), and the plain-mode staging composer with the mode
// toggle. Captures a PNG per state to artifacts_headless/ (visual review, not pass/fail).
public class CommitComposerRenderHarness
{
    [AvaloniaFact]
    public void Capture_StructuredComposer_FilledWithWarning()
    {
        var vm = new CommitComposerViewModel
        {
            Type = "feat",
            Scope = "api",
            // Long enough that the assembled subject exceeds the 72-char soft limit → amber counter + warning chip.
            Description = "add cursor-based pagination to the collection list endpoints and responses",
            Body = "Adds a `cursor` query parameter to every collection endpoint and returns the next cursor in the payload.",
        };
        vm.NewCoAuthor = "Jane Doe <jane@example.com>";
        vm.AddCoAuthorCommand.Execute(null);
        vm.NewIssue = "#128";
        vm.AddIssueCommand.Execute(null);

        var view = new CommitComposerView { DataContext = vm };
        var win = Host(view, 480, 660);
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "commit_composer_structured.png"));

        Assert.True(vm.DescriptionOverLimit);
        Assert.True(vm.HasIssues);
        Assert.False(vm.HasErrors);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_PlainComposer_WithModeToggle()
    {
        var prefs = new UserPreferences { UseStructuredCommitComposer = false };
        var vm = new StagingPanelViewModel(new FakeGitService(), "/repo", onCommitAction: () => { },
            scanner: new PreCommitScanner(new FakeGitService()), preferences: () => prefs);
        vm.CommitMessage = "wip: quick note before switching to the composer";

        var view = new StagingPanelView { DataContext = vm };
        var win = Host(view, 460, 560);
        win.Show();
        Settle();
        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "commit_composer_plain.png"));

        Assert.False(vm.UseStructuredComposer);
        HarnessHygiene.Teardown(win);
    }

    private static Window Host(Control content, double w, double h)
    {
        var root = new Border { Padding = new Thickness(16), Child = content };
        if (Application.Current!.TryGetResource("SurfaceWindow", Application.Current.ActualThemeVariant, out var bg)
            && bg is IBrush brush)
        {
            root.Background = brush;
        }
        return new Window { Width = w, Height = h, Content = root };
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
