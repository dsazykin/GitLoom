using System;
using System.IO;
using System.Threading;
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

// TI-33 — proves the T-11 blame gutter + T-32 "why this line" popover are actually MOUNTED: renders the
// real BlameWindow (the dialog RepoDashboardViewModel.OpenBlameAsync opens from the "Blame this file"
// menus), which hosts the BlameView. The window's own OnOpened path turns blame on and loads the file, so
// this exercises exactly what a user sees after clicking the entry point. PNG to artifacts_headless/.
public class BlameWindowRenderHarness
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
    public void Capture_HostedBlameWindow()
    {
        using var fx = new TempRepoFixture();
        var git = new GitService();
        var body = string.Join("\n", new[]
        {
            "public class Loom",
            "{",
            "    // wired through the Blame this file entry point",
            "    void Weave() { }",
            "}",
        }) + "\n";
        var sha = fx.CommitFile("Loom.cs", body, "add Loom",
            "Ada Lovelace", "ada@gitloom.local", DateTimeOffset.Now.AddDays(-2));

        var context = new CommitContextResult
        {
            Sha = sha,
            PullRequests = new[]
            {
                new PullRequestItem { Number = 42, Title = "Blame → PR jump", State = PullRequestState.Merged,
                    Url = "https://github.com/octocat/hello-world/pull/42" },
            },
            LinkedIssues = new[]
            {
                new LinkedIssueRef { Number = 12, RepoFullName = "octocat/hello-world" },
            },
        };

        // Constructed exactly like OpenBlameAsync: git + repo path + live-shaped context service + sinks,
        // with the file pre-set. The window's OnOpened turns blame on and loads it.
        var vm = new BlameViewModel(git, fx.RepoPath, new StubContextService(context),
            openPullRequest: _ => { }, openLinkedIssue: _ => { })
        {
            FilePath = "Loom.cs",
        };
        var win = new BlameWindow { DataContext = vm, Width = 940, Height = 460 };
        win.Show();

        Pump(() => vm.BlameLines.Count > 0 && !vm.IsLoading);

        // Drive the right-click "why this line" popover so the capture proves T-32 is live in the mount.
        _ = vm.ShowCommitContextAsync(sha);
        Pump(() => vm.LineContext != null && !vm.IsContextBusy);
        Settle();

        Assert.True(vm.IsBlameVisible);          // the window opened blame
        Assert.NotEmpty(vm.BlameLines);          // the gutter has rows
        Assert.NotNull(vm.LineContext);          // the popover resolved

        win.CaptureRenderedFrame()?.Save(Path.Combine(ArtifactsDir(), "blame_window_mounted.png"));
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
        while (dir != null && !File.Exists(Path.Combine(dir, "GitLoom.slnx")))
            dir = Directory.GetParent(dir)?.FullName;
        var artifacts = Path.Combine(dir ?? AppContext.BaseDirectory, "artifacts_headless");
        Directory.CreateDirectory(artifacts);
        return artifacts;
    }
}
