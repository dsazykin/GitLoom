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

// Renders the T-27 Notifications inbox offscreen (headless Skia) in its two key states so the layout/theme,
// the reason chips + subject-kind icons, the unread styling (dot + bold title), and the per-item
// mark-read / open affordances can be inspected without a display:
//   1. populated — notifications across two repos with mixed reasons/subjects and read+unread rows served
//      by a canned service (grouped by repo, newest first);
//   2. unsupported/no-token — the graceful sign-in affordance instead of an error.
// Captures a PNG per state to artifacts_headless/ (visual review, not pass/fail).
public class NotificationsRenderHarness
{
    private static NotificationItem N(string id, string repo, NotificationReason reason,
        NotificationSubjectKind kind, string title, bool unread, string url, int day, int hour) => new()
        {
            Id = id,
            RepoFullName = repo,
            Reason = reason,
            Kind = kind,
            Title = title,
            Unread = unread,
            Url = url,
            UpdatedAt = new DateTimeOffset(2026, 7, day, hour, 0, 0, TimeSpan.Zero),
        };

    [AvaloniaFact]
    public void Capture_Notifications_Populated()
    {
        var svc = new FakeNotificationService
        {
            IsSupportedImpl = _ => true,
            ListImpl = (_, _) => new[]
            {
                N("3001", "octocat/hello-world", NotificationReason.ReviewRequested, NotificationSubjectKind.PullRequest,
                    "Add notifications inbox (T-27)", true, "https://github.com/octocat/hello-world/pull/512", 2, 9),
                N("3002", "octocat/hello-world", NotificationReason.Mention, NotificationSubjectKind.Issue,
                    "Crash on startup when repo name contains an emoji 🚀", true, "https://github.com/octocat/hello-world/issues/101", 1, 14),
                N("3003", "octocat/hello-world", NotificationReason.CiActivity, NotificationSubjectKind.Commit,
                    "build failed on main", false, "https://github.com/octocat/hello-world/commit/9b3ea4b", 1, 6),
                N("3004", "danielsazykin/mainguard", NotificationReason.Subscribed, NotificationSubjectKind.Release,
                    "v2.1.0", true, "https://github.com/danielsazykin/mainguard/releases/tag/v2.1.0", 1, 8),
                N("3005", "danielsazykin/mainguard", NotificationReason.TeamMention, NotificationSubjectKind.Discussion,
                    "How should we structure the plugin API?", false, "", 1, 3),
            },
        };

        var vm = new NotificationsViewModel(svc, "/repo") { UnreadOnly = false };
        var win = new NotificationsWindow { DataContext = vm };
        win.Show();
        vm.RefreshCommand.Execute(null);
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "notifications_populated.png"));

        Assert.True(vm.IsSupported);
        Assert.Equal(2, vm.Groups.Count);
        Assert.Contains(vm.Groups.SelectMany(g => g.Items), r => r.Unread);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_Notifications_Unsupported()
    {
        var svc = new FakeNotificationService { IsSupportedImpl = _ => false };
        var vm = new NotificationsViewModel(svc, "/repo");
        var win = new NotificationsWindow { DataContext = vm };
        win.Show();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "notifications_unsupported.png"));

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
