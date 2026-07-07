using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Issues panel (T-24), the sibling of <see cref="PullRequestsViewModel"/>: lists the repo's issues
/// (number / title / author / label chips / assignees / comment count / updated-at), toggles the
/// Open/Closed filter, opens a New-issue form (title + body + optional labels/assignees), and drives
/// per-issue Comment / Close·Reopen / Open-in-browser through the host-agnostic <see cref="IIssueService"/>.
/// When the origin host is unsupported or no token is stored it shows a graceful sign-in / unsupported
/// affordance instead of erroring.
///
/// <para>All network work runs inside the async service (off the UI thread) and is gated by
/// <see cref="IsBusy"/>; the bound <see cref="Issues"/> collection is only ever mutated on the
/// <see cref="Dispatcher.UIThread"/>. Hosted by IssuesWindow.</para>
/// </summary>
public partial class IssuesViewModel : ViewModelBase
{
    private readonly IIssueService _issues;
    private readonly string _repoPath;
    private readonly Action<string> _openUrl;
    private CancellationTokenSource? _cts;

    public ObservableCollection<IssueRowViewModel> Issues { get; } = new();

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

    /// <summary>False = Open filter, true = Closed filter. Flipping it reloads the list.</summary>
    [ObservableProperty]
    private bool _showClosed;

    // ---- New-issue form -----------------------------------------------------------------------

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private string _newTitle = "";

    [ObservableProperty]
    private string _newBody = "";

    /// <summary>Comma-separated labels for the new issue (optional).</summary>
    [ObservableProperty]
    private string _newLabels = "";

    /// <summary>Comma-separated assignee logins for the new issue (optional).</summary>
    [ObservableProperty]
    private string _newAssignees = "";

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public IssuesViewModel(IIssueService issues, string repoPath, Action<string>? openUrl = null)
    {
        _issues = issues;
        _repoPath = repoPath;
        _openUrl = openUrl ?? Services.BrowserLauncher.OpenUrl;

        IsSupported = SafeIsSupported();
        if (!IsSupported)
        {
            UnsupportedHint =
                "Issue tracking isn't available for this repository yet. Connect an account for the origin host " +
                "(GitHub is supported today) from Accounts, then reopen this panel.";
        }
    }

    private bool SafeIsSupported()
    {
        try { return _issues.IsSupported(_repoPath); }
        catch { return false; }
    }

    // ---- Filter ------------------------------------------------------------------------------

    public string CurrentFilterText => ShowClosed ? "Closed" : "Open";

    private IssueState FilterState => ShowClosed ? IssueState.Closed : IssueState.Open;

    partial void OnShowClosedChanged(bool value)
    {
        OnPropertyChanged(nameof(CurrentFilterText));
        if (IsSupported)
            _ = RefreshList();
    }

    [RelayCommand]
    private void ShowOpen() { if (ShowClosed) ShowClosed = false; }

    [RelayCommand]
    private void ShowClosedIssues() { if (!ShowClosed) ShowClosed = true; }

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
            var items = await _issues.ListAsync(_repoPath, FilterState, ct);
            await ApplyOnUiAsync(() =>
            {
                Issues.Clear();
                foreach (var item in items)
                    Issues.Add(new IssueRowViewModel(item, this));
                IsEmpty = Issues.Count == 0;
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

    private bool CanBeginCreate => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginCreate))]
    private void BeginCreate()
    {
        ErrorMessage = null;
        IsCreating = true;
    }

    [RelayCommand]
    private void CancelCreate() => IsCreating = false;

    private bool CanSubmitCreate =>
        IsSupported && !IsBusy && !string.IsNullOrWhiteSpace(NewTitle);

    [RelayCommand(CanExecute = nameof(CanSubmitCreate))]
    private async Task SubmitCreate()
    {
        if (!CanSubmitCreate) return;
        IsBusy = true;
        ErrorMessage = null;
        var request = new CreateIssue
        {
            Title = NewTitle.Trim(),
            Body = NewBody,
            Labels = SplitCsv(NewLabels),
            Assignees = SplitCsv(NewAssignees),
        };
        try
        {
            await _issues.CreateAsync(_repoPath, request, CancellationToken.None);
            await ApplyOnUiAsync(() =>
            {
                IsCreating = false;
                NewTitle = NewBody = NewLabels = NewAssignees = "";
            });
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null && !ShowClosed)
            await RefreshList();
    }

    private static IReadOnlyList<string> SplitCsv(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ---- Per-row actions ---------------------------------------------------------------------

    internal async Task SetStateAsync(IssueRowViewModel row)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        var target = row.IsClosed ? IssueState.Open : IssueState.Closed;
        try
        {
            await _issues.SetStateAsync(_repoPath, row.Number, target, CancellationToken.None);
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

    internal async Task CommentAsync(IssueRowViewModel row, string body)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(body)) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _issues.CommentAsync(_repoPath, row.Number, body.Trim(), CancellationToken.None);
            await ApplyOnUiAsync(row.EndComment);
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

    internal void OpenInBrowser(IssueRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.Url))
            _openUrl(row.Url);
    }

    // ---- Plumbing ----------------------------------------------------------------------------

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();
    partial void OnIsSupportedChanged(bool value) => NotifyCommandStates();
    partial void OnNewTitleChanged(string value) => SubmitCreateCommand.NotifyCanExecuteChanged();

    private void NotifyCommandStates()
    {
        RefreshListCommand.NotifyCanExecuteChanged();
        BeginCreateCommand.NotifyCanExecuteChanged();
        SubmitCreateCommand.NotifyCanExecuteChanged();
    }

    // Applies a mutation to bound state on the UI thread (invariant G-5): never mutates the observable
    // collection off-thread. Runs inline when already on the UI thread.
    private static Task ApplyOnUiAsync(Action apply)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            apply();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(apply).GetTask();
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One issue row (T-24): number/title/author/label chips/assignees/comment-count/updated-at with
/// per-issue Close·Reopen / inline Comment / Open-in-browser routed back through the parent.</summary>
public partial class IssueRowViewModel : ViewModelBase
{
    private readonly IssuesViewModel _parent;

    public int Number { get; }
    public string Title { get; }
    public string Author { get; }
    public IssueState State { get; }
    public int CommentCount { get; }
    public string Url { get; }
    public DateTimeOffset UpdatedAt { get; }
    public IReadOnlyList<IssueLabelChipViewModel> Labels { get; }
    public IReadOnlyList<string> Assignees { get; }

    public IssueRowViewModel(IssueItem item, IssuesViewModel parent)
    {
        _parent = parent;
        Number = item.Number;
        Title = string.IsNullOrEmpty(item.Title) ? "(no title)" : item.Title;
        Author = item.Author;
        State = item.State;
        CommentCount = item.CommentCount;
        Url = item.Url;
        UpdatedAt = item.UpdatedAt;
        Labels = item.Labels.Select(l => new IssueLabelChipViewModel(l)).ToList();
        Assignees = item.Assignees;
    }

    public string NumberText => $"#{Number}";
    public string AuthorText => string.IsNullOrEmpty(Author) ? "" : $"by {Author}";
    public bool IsClosed => State == IssueState.Closed;
    public string StateActionText => IsClosed ? "Reopen" : "Close";
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasLabels => Labels.Count > 0;
    public bool HasAssignees => Assignees.Count > 0;
    public string AssigneesText => Assignees.Count == 0 ? "" : "@" + string.Join(", @", Assignees);
    public string CommentCountText => $"{CommentCount} 💬";
    public bool HasComments => CommentCount > 0;
    public string UpdatedText => UpdatedAt == default ? "" : $"updated {UpdatedAt.LocalDateTime:MMM d, yyyy}";

    // ---- Inline comment box ------------------------------------------------------------------

    [ObservableProperty]
    private bool _isCommenting;

    [ObservableProperty]
    private string _commentDraft = "";

    [RelayCommand]
    private void BeginComment() => IsCommenting = true;

    [RelayCommand]
    private void CancelComment() => EndComment();

    private bool CanSubmitComment => !string.IsNullOrWhiteSpace(CommentDraft);

    [RelayCommand(CanExecute = nameof(CanSubmitComment))]
    private Task SubmitComment() => _parent.CommentAsync(this, CommentDraft);

    partial void OnCommentDraftChanged(string value) => SubmitCommentCommand.NotifyCanExecuteChanged();

    internal void EndComment()
    {
        IsCommenting = false;
        CommentDraft = "";
    }

    [RelayCommand]
    private Task ToggleState() => _parent.SetStateAsync(this);

    [RelayCommand]
    private void OpenInBrowser() => _parent.OpenInBrowser(this);
}

/// <summary>
/// A label chip (T-24). The host's label hex is <b>data</b> (the one allowed non-token color), so the chip
/// background is painted from it with an auto-contrast readable foreground (black on light labels, white on
/// dark) computed from perceived luminance. Everything else in the UI uses design tokens.
/// </summary>
public sealed class IssueLabelChipViewModel
{
    public string Name { get; }
    public IBrush Background { get; }
    public IBrush Foreground { get; }

    public IssueLabelChipViewModel(IssueLabel label)
    {
        Name = label.Name;
        var color = ParseHex(label.Color);
        Background = new SolidColorBrush(color);
        Foreground = new SolidColorBrush(IsLight(color) ? Colors.Black : Colors.White);
    }

    // GitHub label colors are 6-digit hex without '#'. Unknown/empty → a neutral mid-gray fallback so a
    // color-less label still reads as a chip (still data-driven, never an app-theme color).
    private static Color ParseHex(string hex)
    {
        var s = (hex ?? "").TrimStart('#');
        if (s.Length == 6 &&
            byte.TryParse(s.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r) &&
            byte.TryParse(s.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g) &&
            byte.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return Color.FromRgb(r, g, b);
        }
        return Color.FromRgb(0x8A, 0x93, 0xA6);
    }

    // Perceived luminance (ITU-R BT.601). Above the mid threshold reads as "light" → wants dark text.
    private static bool IsLight(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) > 140.0;
}
