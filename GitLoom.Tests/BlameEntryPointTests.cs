using System.Collections.Generic;
using System.Threading.Tasks;
using GitLoom.App.ViewModels;
using Mainguard.Git.Models;
using GitLoom.Tests.Fakes;
using LibGit2Sharp;
using Xunit;

namespace GitLoom.Tests;

/// <summary>
/// TI-33 — the blame entry points that make the T-11 gutter and T-32 "Why this line" popover reachable.
/// The diff-viewer and staging-panel "Blame this file" commands must raise the host events that
/// <c>RepoDashboardViewModel.OpenBlameAsync</c> turns into a blame dialog, and a <see cref="BlameViewModel"/>
/// constructed with a live commit-context service + routing sinks must load a file and route its popover
/// jumps out through those sinks.
/// </summary>
public class BlameEntryPointTests
{
    [Fact]
    public void DiffViewer_ShowBlameCommand_RaisesBlameRequested_WithFilePath()
    {
        var fake = new FakeGitService { GetFileDiffWhitespaceImpl = (_, _, _, _) => "" };
        var vm = new DiffViewerViewModel(fake, "/repo");
        vm.UpdateDiff(new GitFileStatus { FilePath = "src/app.cs", State = FileStatus.ModifiedInWorkdir });

        string? requested = null;
        vm.BlameRequested += p => requested = p;

        Assert.True(vm.ShowBlameCommand.CanExecute(null));
        vm.ShowBlameCommand.Execute(null);

        Assert.Equal("src/app.cs", requested);
    }

    [Fact]
    public void StagingPanel_ShowBlameCommand_RaisesOnBlameRequested_WithFilePath()
    {
        var fake = new FakeGitService();
        var vm = new StagingPanelViewModel(fake, "/repo", onCommitAction: () => { });

        string? requested = null;
        vm.OnBlameRequested += p => requested = p;

        var file = new GitFileStatus { FilePath = "docs/readme.md", State = FileStatus.ModifiedInWorkdir };
        vm.ShowBlameCommand.Execute(file);

        Assert.Equal("docs/readme.md", requested);
    }

    [Fact]
    public async Task BlameViewModel_WithContextServiceAndSinks_LoadsFile_AndRoutesPopoverJumps()
    {
        var blame = new List<BlameLine> { new() { LineNumber = 1, Sha = "deadbeefdeadbeef", ShortSha = "deadbee" } };
        var fake = new FakeGitService { GetBlameImpl = (_, _, _) => blame };

        var context = new FakeCommitContextService
        {
            IsSupportedImpl = _ => true,
            GetForCommitImpl = (_, sha) => new CommitContextResult
            {
                Sha = sha,
                PullRequests = new[] { new PullRequestItem { Number = 7, Title = "Fix it", State = PullRequestState.Merged } },
                LinkedIssues = new[] { new LinkedIssueRef { Number = 3, RepoFullName = "octocat/hello-world" } },
            },
        };

        var openedPrs = new List<PullRequestItem>();
        var openedIssues = new List<LinkedIssueRef>();
        var vm = new BlameViewModel(fake, "/repo", context, openedPrs.Add, openedIssues.Add)
        {
            IsBlameVisible = true,
        };

        Assert.True(vm.IsCommitContextSupported);   // the gutter offers its "why this line" jump

        // The dialog's open path: load blame for the pre-set file.
        await vm.LoadAsync("src/app.cs");
        Assert.Same(blame, vm.BlameLines);
        Assert.Equal("src/app.cs", vm.FilePath);

        // Right-click → resolve the commit's PR/issue context; the popover opens.
        await vm.ShowCommitContextAsync("deadbeefdeadbeef");
        Assert.NotNull(vm.LineContext);

        // The popover's jumps route the right models out through the host-supplied sinks.
        vm.LineContext!.GoToPullRequestCommand.Execute(null);
        var pr = Assert.Single(openedPrs);
        Assert.Equal(7, pr.Number);

        vm.LineContext!.GoToLinkedIssueCommand.Execute(null);
        var issue = Assert.Single(openedIssues);
        Assert.Equal(3, issue.Number);
    }
}
