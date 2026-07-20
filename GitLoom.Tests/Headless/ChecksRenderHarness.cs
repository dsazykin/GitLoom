using System;
using System.IO;
using System.Linq;
using System.Threading;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using GitLoom.App.ViewModels;
using GitLoom.App.Views;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using GitLoom.Tests.Fakes;
using Xunit;

namespace GitLoom.Tests.Headless;

// Renders the T-26 CI Checks panel offscreen (headless Skia) in its two key states so the layout/theme,
// the overall status badge, and the per-run state icons / view-logs / re-run affordances can be inspected
// without a display:
//   1. mixed — a run list with success/failure/pending/neutral rows served by a canned service, with the
//      overall badge showing the failing roll-up;
//   2. unsupported/no-token — the graceful sign-in affordance instead of an error.
// Captures a PNG per state to artifacts_headless/ (visual review, not pass/fail).
public class ChecksRenderHarness
{
    private static CommitChecks MixedChecks() => CheckStateMapper.Rollup("9b3ea4bcafe", new[]
    {
        new CheckRunItem { Id = 8001, Name = "build (ubuntu-latest)", State = CheckState.Success, DetailsUrl = "https://github.com/octocat/hello-world/runs/8001", CompletedAt = new DateTimeOffset(2026, 7, 1, 10, 5, 0, TimeSpan.Zero) },
        new CheckRunItem { Id = 8002, Name = "test (windows-latest)", State = CheckState.Failure, DetailsUrl = "https://github.com/octocat/hello-world/runs/8002", CompletedAt = new DateTimeOffset(2026, 7, 1, 10, 6, 0, TimeSpan.Zero) },
        new CheckRunItem { Id = 8003, Name = "lint & format", State = CheckState.Pending, DetailsUrl = "https://github.com/octocat/hello-world/runs/8003" },
        new CheckRunItem { Id = 8004, Name = "coverage upload", State = CheckState.Neutral, DetailsUrl = "https://github.com/octocat/hello-world/runs/8004", CompletedAt = new DateTimeOffset(2026, 7, 1, 10, 4, 0, TimeSpan.Zero) },
        new CheckRunItem { Id = 0, Name = "deploy-preview", State = CheckState.Success, DetailsUrl = "https://deploy.example.com/preview/123" },
    });

    [AvaloniaFact]
    public void Capture_Checks_Mixed()
    {
        var svc = new FakeCheckStatusService
        {
            IsSupportedImpl = _ => true,
            GetChecksImpl = (_, _) => MixedChecks(),
        };

        var vm = new ChecksViewModel(svc, "/repo", "9b3ea4bcafe");
        var win = new ChecksWindow { DataContext = vm };
        win.Show();
        vm.RefreshCommand.Execute(null);
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "checks_mixed.png"));

        Assert.True(vm.IsSupported);
        Assert.Equal(5, vm.Runs.Count);
        Assert.True(vm.Badge.IsVisible);
        Assert.True(vm.Badge.IsFailure);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_Checks_Unsupported()
    {
        var svc = new FakeCheckStatusService { IsSupportedImpl = _ => false };
        var vm = new ChecksViewModel(svc, "/repo", "9b3ea4bcafe");
        var win = new ChecksWindow { DataContext = vm };
        win.Show();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "checks_unsupported.png"));

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
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
