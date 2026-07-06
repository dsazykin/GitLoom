using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The "why behind this line" popover (T-32) shown from the T-11 blame gutter: for a clicked commit it
/// lists the pull request(s) that introduced/contain it and the issue(s) those PRs reference, and routes
/// a jump into the PR / Issues panel (or the browser). Routing is done through injected sinks so the VM
/// holds no window/browser logic and stays unit-testable:
/// <list type="bullet">
///   <item>one PR → <c>GoToPullRequest</c> opens it directly;</item>
///   <item>several PRs → <c>GoToPullRequest</c> reveals a chooser (<see cref="IsChoosingPullRequest"/>);</item>
///   <item>no PR → the command is disabled;</item>
///   <item>linked issues route per-row (and the single-issue case via <c>GoToLinkedIssue</c>).</item>
/// </list>
/// </summary>
public partial class BlameCommitContextViewModel : ViewModelBase
{
    private readonly Action<PullRequestItem> _openPullRequest;
    private readonly Action<LinkedIssueRef> _openLinkedIssue;

    public string Sha { get; }

    public IReadOnlyList<CommitContextPrRowViewModel> PullRequests { get; }
    public IReadOnlyList<CommitContextIssueRowViewModel> LinkedIssues { get; }

    public BlameCommitContextViewModel(
        CommitContextResult result,
        Action<PullRequestItem> openPullRequest,
        Action<LinkedIssueRef> openLinkedIssue)
    {
        if (result is null) throw new ArgumentNullException(nameof(result));
        _openPullRequest = openPullRequest ?? throw new ArgumentNullException(nameof(openPullRequest));
        _openLinkedIssue = openLinkedIssue ?? throw new ArgumentNullException(nameof(openLinkedIssue));

        Sha = result.Sha ?? "";
        PullRequests = result.PullRequests.Select(pr => new CommitContextPrRowViewModel(pr, this)).ToList();
        LinkedIssues = result.LinkedIssues.Select(i => new CommitContextIssueRowViewModel(i, this)).ToList();
    }

    public string ShortSha => Sha.Length >= 7 ? Sha[..7] : Sha;

    public bool HasPullRequests => PullRequests.Count > 0;
    public bool HasSinglePullRequest => PullRequests.Count == 1;
    public bool HasMultiplePullRequests => PullRequests.Count > 1;
    public bool HasLinkedIssues => LinkedIssues.Count > 0;

    /// <summary>True when the commit has no associated PR and no linked issue — the popover shows an "nothing" hint.</summary>
    public bool HasNothing => !HasPullRequests && !HasLinkedIssues;

    /// <summary>Revealed when several PRs contain the commit so the user can pick which one to jump to.</summary>
    [ObservableProperty]
    private bool _isChoosingPullRequest;

    // ---- Pull request jump --------------------------------------------------------------------

    private bool CanGoToPullRequest => HasPullRequests;

    [RelayCommand(CanExecute = nameof(CanGoToPullRequest))]
    private void GoToPullRequest()
    {
        if (HasSinglePullRequest)
            _openPullRequest(PullRequests[0].Model);
        else if (HasMultiplePullRequests)
            IsChoosingPullRequest = true;   // several — let the user pick
    }

    // Routed from a chooser row (or a single-PR row) — opens that specific PR.
    internal void OpenPullRequest(PullRequestItem pr)
    {
        IsChoosingPullRequest = false;
        _openPullRequest(pr);
    }

    // ---- Linked issue jump --------------------------------------------------------------------

    private bool CanGoToLinkedIssue => LinkedIssues.Count == 1;

    [RelayCommand(CanExecute = nameof(CanGoToLinkedIssue))]
    private void GoToLinkedIssue()
    {
        if (LinkedIssues.Count == 1)
            _openLinkedIssue(LinkedIssues[0].Model);
    }

    internal void OpenLinkedIssue(LinkedIssueRef issue) => _openLinkedIssue(issue);
}

/// <summary>One pull-request row in the blame context popover (T-32): number/title/state, opens on click.</summary>
public partial class CommitContextPrRowViewModel : ViewModelBase
{
    private readonly BlameCommitContextViewModel _parent;

    internal PullRequestItem Model { get; }

    public int Number { get; }
    public string Title { get; }
    public string Url { get; }
    public PullRequestState State { get; }

    public CommitContextPrRowViewModel(PullRequestItem item, BlameCommitContextViewModel parent)
    {
        _parent = parent;
        Model = item;
        Number = item.Number;
        Title = string.IsNullOrEmpty(item.Title) ? "(no title)" : item.Title;
        Url = item.Url;
        State = item.State;
    }

    public string NumberText => $"#{Number}";
    public string StateText => State switch
    {
        PullRequestState.Merged => "merged",
        PullRequestState.Closed => "closed",
        PullRequestState.Draft => "draft",
        _ => "open",
    };

    [RelayCommand]
    private void Open() => _parent.OpenPullRequest(Model);
}

/// <summary>One linked-issue row in the blame context popover (T-32): <c>owner/repo#n</c> (or bare <c>#n</c>), opens on click.</summary>
public partial class CommitContextIssueRowViewModel : ViewModelBase
{
    private readonly BlameCommitContextViewModel _parent;

    internal LinkedIssueRef Model { get; }

    public int Number { get; }
    public string RepoFullName { get; }

    public CommitContextIssueRowViewModel(LinkedIssueRef issue, BlameCommitContextViewModel parent)
    {
        _parent = parent;
        Model = issue;
        Number = issue.Number;
        RepoFullName = issue.RepoFullName ?? "";
    }

    /// <summary>Displays a cross-repo ref as <c>owner/repo#7</c> and a same-repo ref as bare <c>#7</c>.</summary>
    public string DisplayText => string.IsNullOrEmpty(RepoFullName) ? $"#{Number}" : $"{RepoFullName}#{Number}";

    [RelayCommand]
    private void Open() => _parent.OpenLinkedIssue(Model);
}
