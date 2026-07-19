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
using Mainguard.Git.Models;
using Mainguard.Tests.Fakes;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-28 Releases panel offscreen (headless Skia) in its two key states so the layout/theme, the
// Draft/Prerelease badges, and the composer with auto-generated notes can be inspected without a display:
//   1. populated — an existing-release list (stable / prerelease / draft) with the New-release composer open
//      and its body pre-filled from a generated changelog;
//   2. unsupported/no-token — the graceful sign-in affordance instead of an error.
// Captures a PNG per state to artifacts_headless/ (visual review, not pass/fail).
public class ReleasesRenderHarness
{
    private static FakeGitService Git() => new()
    {
        GetBranchesImpl = _ => new[] { new GitBranchItem { FriendlyName = "main", IsCurrentRepositoryHead = true } },
    };

    private static ReleaseItem Rel(string tag, string name, bool draft, bool pre, string body, DateTimeOffset? published) => new()
    {
        TagName = tag,
        Name = name,
        IsDraft = draft,
        IsPrerelease = pre,
        Body = body,
        Author = "octocat",
        PublishedAt = published,
        Url = $"https://github.com/octocat/hello-world/releases/tag/{tag}",
    };

    [AvaloniaFact]
    public void Capture_Releases_ListAndComposer()
    {
        var svc = new FakeReleaseService
        {
            IsSupportedImpl = _ => true,
            ListImpl = _ => new[]
            {
                Rel("v2.0.0", "GitLoom 2.0", draft: false, pre: false, "shiny", new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero)),
                Rel("v2.1.0-rc1", "GitLoom 2.1 RC1", draft: false, pre: true, "rc", new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.Zero)),
                Rel("v2.2.0", "GitLoom 2.2 (draft)", draft: true, pre: false, "wip", null),
            },
        };

        var vm = new ReleasesViewModel(svc, Git(), "/repo");
        var win = new ReleasesWindow { DataContext = vm };
        win.Show();
        vm.RefreshListCommand.Execute(null);

        // Open the composer with generated notes shown.
        vm.IsComposing = true;
        vm.NewTagName = "v3.0.0";
        vm.NewName = "GitLoom 3.0";
        vm.NewBody =
            "### Breaking Changes\n- **api:** drop v1 endpoints (ccccccc)\n\n" +
            "### Features\n- add releases panel (aaaaaaa)\n\n" +
            "### Fixes\n- **core:** npe on empty repo (bbbbbbb)\n\n" +
            "**Full changelog:** v2.2.0...v3.0.0";
        vm.NewIsDraft = true;
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "releases_list_composer.png"));

        Assert.True(vm.IsSupported);
        Assert.Equal(3, vm.Releases.Count);
        Assert.True(vm.IsComposing);
        HarnessHygiene.Teardown(win);
    }

    [AvaloniaFact]
    public void Capture_Releases_Unsupported()
    {
        var svc = new FakeReleaseService { IsSupportedImpl = _ => false };
        var vm = new ReleasesViewModel(svc, Git(), "/repo");
        var win = new ReleasesWindow { DataContext = vm };
        win.Show();
        Settle();

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "releases_unsupported.png"));

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
