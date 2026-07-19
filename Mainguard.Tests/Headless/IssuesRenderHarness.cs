using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Tests.Fakes;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-24 Issues panel offscreen (headless Skia) in its two key states so the layout/theme,
// the host-colored label chips, and the per-issue Comment/Close/Open affordances can be inspected
// without a display:
//   1. populated — an issue list (with bug/enhancement/priority label chips + assignees) served by a
//      canned service (the mixed PR row is already filtered by the provider, so it never reaches here);
//   2. unsupported/no-token — the graceful sign-in affordance instead of an error.
// Captures a PNG per state to artifacts_headless/ (visual review, not pass/fail).
public class IssuesRenderHarness
{
    private static IssueItem Issue(int n, string title, string author, IssueState state, int comments,
        (string name, string color)[] labels, string[] assignees) => new()
        {
            Number = n,
            Title = title,
            Author = author,
            State = state,
            CommentCount = comments,
            Labels = labels.Select(l => new IssueLabel { Name = l.name, Color = l.color }).ToList(),
            Assignees = assignees,
            Url = $"https://github.com/octocat/hello-world/issues/{n}",
            UpdatedAt = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero),
        };

    [AvaloniaFact]
    public void Capture_Issues_Populated()
    {
        var svc = new FakeIssueService
        {
            IsSupportedImpl = _ => true,
            ListImpl = (_, _) => new[]
            {
                Issue(101, "Crash on startup when repo name contains an emoji 🚀", "danielsazykin", IssueState.Open, 3,
                    new[] { ("bug", "d73a4a"), ("priority: high", "b60205") }, new[] { "octocat", "hubot" }),
                Issue(100, "Add a dark-mode toggle to the toolbar", "hubot", IssueState.Open, 0,
                    new[] { ("enhancement", "a2eeef") }, Array.Empty<string>()),
                Issue(98, "Docs: document the reflog recovery flow", "octocat", IssueState.Open, 1,
                    new[] { ("documentation", "0075ca"), ("good first issue", "7057ff") }, new[] { "danielsazykin" }),
            },
        };

        var vm = new IssuesViewModel(svc, "/repo");
        var win = new IssuesWindow { DataContext = vm };
        win.Show();
        vm.RefreshListCommand.Execute(null);
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "issues_populated.png"));

        Assert.True(vm.IsSupported);
        Assert.Equal(3, vm.Issues.Count);
        Assert.Contains(vm.Issues, r => r.HasLabels);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_Issues_Unsupported()
    {
        var svc = new FakeIssueService { IsSupportedImpl = _ => false };
        var vm = new IssuesViewModel(svc, "/repo");
        var win = new IssuesWindow { DataContext = vm };
        win.Show();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "issues_unsupported.png"));

        Assert.False(vm.IsSupported);
        Assert.False(string.IsNullOrWhiteSpace(vm.UnsupportedHint));
        HarnessHygiene.Teardown(win);
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
