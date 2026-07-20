using System;
using System.IO;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Mainguard.Agents.Services;
using Mainguard.Agents.UI.ViewModels;
using Mainguard.Agents.UI.Views;
using Mainguard.App.Shell.ViewModels;
using Mainguard.App.Shell.Views;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using Mainguard.Tests.Fixtures;
using Mainguard.UI.ViewModels;
using Mainguard.UI.Views;
using Xunit;

namespace Mainguard.Tests.Headless;

// Renders the T-32 blame → PR/issue "why behind this line" popover offscreen (headless Skia): the blame
// editor + gutter with the context card open, showing the PR(s) that introduced the commit and its linked
// issues. PNG to artifacts_headless/ for the human visual pass.
public class BlameCommitContextRenderHarness
{
    private sealed class StubContextService : ICommitContextService
    {
        private readonly CommitContextResult _result;
        public StubContextService(CommitContextResult result) => _result = result;
        public bool IsSupported(string repoPath) => true;
        public System.Threading.Tasks.Task<CommitContextResult> GetForCommitAsync(string repoPath, string sha, CancellationToken ct)
            => System.Threading.Tasks.Task.FromResult(_result);
    }

    [AvaloniaFact]
    public void Capture_BlameCommitContextPopover()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var body = string.Join("\n", new[] { "public class Loom", "{", "    void Weave() { }", "}" }) + "\n";
        var sha = fx.CommitFile("Loom.cs", body, "add Loom",
            "Ada Lovelace", "ada@mainguard.local", DateTimeOffset.Now.AddDays(-3));

        var context = new CommitContextResult
        {
            Sha = sha,
            PullRequests = new[]
            {
                new PullRequestItem { Number = 42, Title = "Blame → PR jump", State = PullRequestState.Merged,
                    Url = "https://github.com/octocat/hello-world/pull/42" },
                new PullRequestItem { Number = 55, Title = "Backport to release/1.x", State = PullRequestState.Open,
                    Url = "https://github.com/octocat/hello-world/pull/55" },
            },
            LinkedIssues = new[]
            {
                new LinkedIssueRef { Number = 12, RepoFullName = "octocat/hello-world" },
                new LinkedIssueRef { Number = 3, RepoFullName = "octocat/spec" },
            },
        };

        var vm = new BlameViewModel(git, fx.RepoPath, new StubContextService(context)) { IsBlameVisible = true };
        var view = new BlameView { DataContext = vm };
        var win = new Window { Content = view, Width = 940, Height = 460 };
        win.Show();

        _ = vm.LoadAsync("Loom.cs");
        Pump(() => vm.BlameLines.Count > 0 && !vm.IsLoading);

        _ = vm.ShowCommitContextAsync(sha);
        Pump(() => vm.LineContext != null && !vm.IsContextBusy);
        Settle();

        Assert.NotNull(vm.LineContext);
        Assert.True(vm.LineContext!.HasMultiplePullRequests);
        Assert.True(vm.LineContext.HasLinkedIssues);

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "blame_commit_context.png"));
        HarnessHygiene.Teardown(win);
    }

    private static void Settle()
    {
        for (int i = 0; i < 8; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(30); }
    }

    private static void Pump(Func<bool> until)
    {
        for (int i = 0; i < 200 && !until(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        Dispatcher.UIThread.RunJobs();
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
