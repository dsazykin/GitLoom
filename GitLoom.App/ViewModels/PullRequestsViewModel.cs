using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Pull Requests panel (T-23): lists the repo's open PRs (title / number / author / source→target,
/// with a draft badge) and drives Create / Merge (method picker) / Close / Open-in-browser through
/// the host-agnostic <see cref="IPullRequestService"/>. When the origin host is unsupported or no
/// token is stored, it shows a graceful sign-in / unsupported affordance instead of erroring.
///
/// <para>All network work runs inside the async service (off the UI thread) and is gated by
/// <see cref="IsBusy"/>; the bound <see cref="PullRequests"/> collection is only ever mutated on the
/// <see cref="Dispatcher.UIThread"/>. Create is disabled on a detached/unborn HEAD (nothing to open a
/// PR from) with a hint to push a branch first. Hosted by PullRequestsWindow.</para>
/// </summary>
public partial class PullRequestsViewModel : ViewModelBase
{
    private readonly IPullRequestService _pr;
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly Action<string> _openUrl;
    private CancellationTokenSource? _cts;

    public ObservableCollection<PullRequestRowViewModel> PullRequests { get; } = new();

    [ObservableProperty]
    private bool _isSupported;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isEmpty = true;

    /// <summary>Shown when <see cref="IsSupported"/> is false — the unsupported-host / sign-in affordance text.</summary>
    [ObservableProperty]
    private string _unsupportedHint = "";

    // ---- Create-PR form -----------------------------------------------------------------------

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string _newTitle = "";

    [ObservableProperty]
    private string _newBody = "";

    [ObservableProperty]
    private string _newSourceBranch = "";

    [ObservableProperty]
    private string _newTargetBranch = "";

    [ObservableProperty]
    private bool _newIsDraft;

    /// <summary>Non-null when Create is disabled (e.g. detached HEAD) — the reason to show the user.</summary>
    [ObservableProperty]
    private string? _createDisabledHint;

    /// <summary>True when the current HEAD is a branch that can be the source of a PR.</summary>
    public bool CanCreate { get; private set; }

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public PullRequestsViewModel(IPullRequestService pr, IGitService git, string repoPath,
        Action<string>? openUrl = null)
    {
        _pr = pr;
        _git = git;
        _repoPath = repoPath;
        _openUrl = openUrl ?? OpenUrlInBrowser;

        IsSupported = SafeIsSupported();
        if (!IsSupported)
        {
            UnsupportedHint =
                "Pull requests aren't available for this repository yet. Connect an account for the origin host " +
                "(GitHub is supported today) from Accounts, then reopen this panel.";
        }

        PrefillCreateForm();
    }

    private bool SafeIsSupported()
    {
        try { return _pr.IsSupported(_repoPath); }
        catch { return false; }
    }

    // Prefills the create form: source = current branch, target = default branch, title = last commit
    // subject. Also decides whether Create is allowed at all (detached/unborn HEAD → disabled + hint).
    private void PrefillCreateForm()
    {
        try
        {
            var head = _git.GetHeadState(_repoPath);
            if (head.IsDetached || head.IsUnborn || string.IsNullOrEmpty(head.CurrentBranchName))
            {
                CanCreate = false;
                CreateDisabledHint = head.IsUnborn
                    ? "This repository has no commits yet — commit before opening a pull request."
                    : "HEAD is detached — check out a branch (and push it) before opening a pull request.";
            }
            else
            {
                CanCreate = true;
                NewSourceBranch = head.CurrentBranchName;
                NewTargetBranch = ResolveDefaultBranch(head.CurrentBranchName);
                NewTitle = LastCommitSubject();
            }
        }
        catch
        {
            CanCreate = false;
            CreateDisabledHint = "Could not read the current branch.";
        }

        OnPropertyChanged(nameof(CanCreate));
        NotifyCommandStates();
    }

    private string ResolveDefaultBranch(string current)
    {
        try
        {
            var locals = _git.GetBranches(_repoPath)
                .Where(b => !b.IsRemote)
                .Select(b => b.FriendlyName)
                .ToList();
            foreach (var candidate in new[] { "main", "master", "develop" })
                if (locals.Contains(candidate) && !string.Equals(candidate, current, StringComparison.Ordinal))
                    return candidate;
            return locals.FirstOrDefault(b => !string.Equals(b, current, StringComparison.Ordinal)) ?? current;
        }
        catch
        {
            return current;
        }
    }

    private string LastCommitSubject()
    {
        try { return _git.GetRecentCommits(_repoPath, 0, 1).FirstOrDefault()?.MessageShort ?? ""; }
        catch { return ""; }
    }

    // ---- List --------------------------------------------------------------------------------

    private bool CanRefresh => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshList()
    {
        if (!IsSupported || IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var items = await _pr.ListAsync(_repoPath, PullRequestState.Open, ct);
            await ApplyOnUiAsync(() =>
            {
                PullRequests.Clear();
                foreach (var item in items)
                    PullRequests.Add(new PullRequestRowViewModel(item, this));
                IsEmpty = PullRequests.Count == 0;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh — ignore */ }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Create ------------------------------------------------------------------------------

    private bool CanBeginCreate => IsSupported && CanCreate && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        ErrorMessage = null;
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    private bool CanSubmitCreate =>
        IsSupported && CanCreate && !IsBusy
        && !string.IsNullOrWhiteSpace(NewTitle)
        && !string.IsNullOrWhiteSpace(NewSourceBranch)
        && !string.IsNullOrWhiteSpace(NewTargetBranch);

    [RelayCommand(CanExecute = nameof(CanSubmitCreate))]
    private async Task SubmitCreate()
    {
        if (!CanSubmitCreate) return;
        IsBusy = true;
        ErrorMessage = null;
        var request = new CreatePullRequest
        {
            Title = NewTitle.Trim(),
            Body = NewBody,
            SourceBranch = NewSourceBranch.Trim(),
            TargetBranch = NewTargetBranch.Trim(),
            IsDraft = NewIsDraft,
        };
        try
        {
            await _pr.CreateAsync(_repoPath, request, CancellationToken.None);
            await ApplyOnUiAsync(() => IsCreating = false);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await RefreshList();
    }

    // ---- Per-row actions ---------------------------------------------------------------------

    internal async Task MergeAsync(PullRequestRowViewModel row, PullRequestMergeMethod method)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _pr.MergeAsync(_repoPath, row.Number, method, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await RefreshList();
    }

    internal async Task CloseAsync(PullRequestRowViewModel row)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _pr.CloseAsync(_repoPath, row.Number, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await RefreshList();
    }

    internal void OpenInBrowser(PullRequestRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.Url))
            _openUrl(row.Url);
    }

    // ---- Plumbing ----------------------------------------------------------------------------

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();
    partial void OnIsSupportedChanged(bool value) => NotifyCommandStates();
    partial void OnNewTitleChanged(string value) => SubmitCreateCommand.NotifyCanExecuteChanged();
    partial void OnNewSourceBranchChanged(string value) => SubmitCreateCommand.NotifyCanExecuteChanged();
    partial void OnNewTargetBranchChanged(string value) => SubmitCreateCommand.NotifyCanExecuteChanged();

    private void NotifyCommandStates()
    {
        RefreshListCommand.NotifyCanExecuteChanged();
        BeginCreateCommand.NotifyCanExecuteChanged();
        SubmitCreateCommand.NotifyCanExecuteChanged();
    }

    // Applies a mutation to bound state on the UI thread (invariant G-5): never mutates the
    // observable collection off-thread. Runs inline when already on the UI thread.
    private static Task ApplyOnUiAsync(Action apply)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            apply();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(apply).GetTask();
    }

    private static void OpenUrlInBrowser(string url)
    {
        try
        {
            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Linux))
                System.Diagnostics.Process.Start("xdg-open", url);
            else if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX))
                System.Diagnostics.Process.Start("open", url);
        }
        catch
        {
            // Opening a browser is best-effort; never crash the panel over it.
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One pull-request row (T-23): number/title/author/source→target with a draft badge, plus the
/// per-PR Merge (method picker) / Close / Open-in-browser affordances routed back through the parent.</summary>
public partial class PullRequestRowViewModel : ViewModelBase
{
    private readonly PullRequestsViewModel _parent;

    public int Number { get; }
    public string Title { get; }
    public string Author { get; }
    public string SourceBranch { get; }
    public string TargetBranch { get; }
    public string Url { get; }
    public bool IsDraft { get; }
    public PullRequestState State { get; }

    public PullRequestRowViewModel(PullRequestItem item, PullRequestsViewModel parent)
    {
        _parent = parent;
        Number = item.Number;
        Title = string.IsNullOrEmpty(item.Title) ? "(no title)" : item.Title;
        Author = item.Author;
        SourceBranch = item.SourceBranch;
        TargetBranch = item.TargetBranch;
        Url = item.Url;
        IsDraft = item.IsDraft;
        State = item.State;
    }

    public string NumberText => $"#{Number}";
    public string BranchFlow => $"{SourceBranch} → {TargetBranch}";
    public string AuthorText => string.IsNullOrEmpty(Author) ? "" : $"by {Author}";
    public bool ShowDraftBadge => IsDraft;
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);

    /// <summary>Merge methods offered by the picker (T-23): a normal merge commit, squash, or rebase.</summary>
    public IReadOnlyList<PullRequestMergeMethod> MergeMethods { get; } =
        new[] { PullRequestMergeMethod.Merge, PullRequestMergeMethod.Squash, PullRequestMergeMethod.Rebase };

    [ObservableProperty]
    private PullRequestMergeMethod _selectedMergeMethod = PullRequestMergeMethod.Merge;

    [RelayCommand]
    private Task Merge() => _parent.MergeAsync(this, SelectedMergeMethod);

    [RelayCommand]
    private Task ClosePr() => _parent.CloseAsync(this);

    [RelayCommand]
    private void OpenInBrowser() => _parent.OpenInBrowser(this);
}
