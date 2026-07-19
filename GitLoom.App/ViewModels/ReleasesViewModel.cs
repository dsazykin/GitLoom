using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Releases panel (T-28), the sibling of <see cref="IssuesViewModel"/>: lists the origin host's releases
/// (tag / name / Draft·Prerelease badges / published date / open-in-browser) and drives a New-release
/// composer — pick an existing tag or name a new tag + target branch, edit the name/body, auto-generate
/// grouped notes from the local commit history, toggle Draft/Prerelease, and Publish — through the
/// host-agnostic <see cref="IReleaseService"/>. When the origin host is unsupported or no token is stored
/// it shows a graceful sign-in / unsupported affordance instead of erroring.
///
/// <para>All network work runs inside the async service (off the UI thread) and is gated by
/// <see cref="IsBusy"/>; note generation is local and runs on a background <see cref="Task"/>. The bound
/// <see cref="Releases"/> collection is only ever mutated on the <see cref="Dispatcher.UIThread"/>.
/// Hosted by ReleasesWindow.</para>
/// </summary>
public partial class ReleasesViewModel : ViewModelBase
{
    private readonly IReleaseService _releases;
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly Action<string> _openUrl;
    private CancellationTokenSource? _cts;

    public ObservableCollection<ReleaseRowViewModel> Releases { get; } = new();

    /// <summary>Existing tag names for the composer's "use an existing tag" picker.</summary>
    public ObservableCollection<string> ExistingTags { get; } = new();

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

    // ---- New-release composer ------------------------------------------------------------------

    [ObservableProperty]
    private bool _isComposing;

    [ObservableProperty]
    private string _newTagName = "";

    /// <summary>Branch or sha a NEW tag is created at (ignored when the tag already exists).</summary>
    [ObservableProperty]
    private string _newTarget = "";

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string _newBody = "";

    [ObservableProperty]
    private bool _newIsDraft;

    [ObservableProperty]
    private bool _newIsPrerelease;

    /// <summary>Picking a tag from the existing-tag list fills <see cref="NewTagName"/>.</summary>
    [ObservableProperty]
    private string? _selectedExistingTag;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public ReleasesViewModel(IReleaseService releases, IGitService git, string repoPath, Action<string>? openUrl = null)
    {
        _releases = releases;
        _git = git;
        _repoPath = repoPath;
        _openUrl = openUrl ?? Services.BrowserLauncher.OpenUrl;

        IsSupported = SafeIsSupported();
        if (!IsSupported)
        {
            UnsupportedHint =
                "Releases aren't available for this repository yet. Connect an account for the origin host " +
                "(GitHub is supported today) from Accounts, then reopen this panel.";
        }

        LoadExistingTags();
        NewTarget = SafeCurrentBranch();
    }

    private bool SafeIsSupported()
    {
        try { return _releases.IsSupported(_repoPath); }
        catch { return false; }
    }

    private void LoadExistingTags()
    {
        try
        {
            foreach (var tag in _git.GetTags(_repoPath).Select(t => t.Name).OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))
                ExistingTags.Add(tag);
        }
        catch { /* a tag read failure just leaves the picker empty */ }
    }

    private string SafeCurrentBranch()
    {
        try
        {
            var head = _git.GetBranches(_repoPath).FirstOrDefault(b => b.IsCurrentRepositoryHead && !b.IsRemote);
            return head?.FriendlyName ?? "";
        }
        catch { return ""; }
    }

    // ---- List ---------------------------------------------------------------------------------

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
            var items = await _releases.ListAsync(_repoPath, ct);
            await ApplyOnUiAsync(() =>
            {
                Releases.Clear();
                foreach (var item in items)
                    Releases.Add(new ReleaseRowViewModel(item, this));
                IsEmpty = Releases.Count == 0;
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

    // ---- Composer -----------------------------------------------------------------------------

    private bool CanBeginCompose => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanBeginCompose))]
    private void BeginCompose()
    {
        ErrorMessage = null;
        IsComposing = true;
    }

    [RelayCommand]
    private void CancelCompose() => IsComposing = false;

    partial void OnSelectedExistingTagChanged(string? value)
    {
        if (!string.IsNullOrEmpty(value))
            NewTagName = value;
    }

    private bool CanGenerateNotes => !IsBusy;

    /// <summary>Local-only: fills the body from the commits since the previous release tag. No network.</summary>
    [RelayCommand(CanExecute = nameof(CanGenerateNotes))]
    private async Task GenerateNotes()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        var tag = string.IsNullOrWhiteSpace(NewTagName) ? "Unreleased" : NewTagName.Trim();
        var target = string.IsNullOrWhiteSpace(NewTarget) ? "HEAD" : NewTarget.Trim();
        try
        {
            var notes = await Task.Run(() => _releases.GenerateNotes(_repoPath, tag, target));
            await ApplyOnUiAsync(() => NewBody = notes);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanPublish =>
        IsSupported && !IsBusy && !string.IsNullOrWhiteSpace(NewTagName);

    [RelayCommand(CanExecute = nameof(CanPublish))]
    private async Task Publish()
    {
        if (!CanPublish) return;
        IsBusy = true;
        ErrorMessage = null;
        var request = new CreateRelease
        {
            TagName = NewTagName.Trim(),
            TargetCommitish = NewTarget.Trim(),
            Name = string.IsNullOrWhiteSpace(NewName) ? NewTagName.Trim() : NewName.Trim(),
            Body = NewBody,
            IsDraft = NewIsDraft,
            IsPrerelease = NewIsPrerelease,
        };
        try
        {
            await _releases.CreateAsync(_repoPath, request, CancellationToken.None);
            await ApplyOnUiAsync(() =>
            {
                IsComposing = false;
                NewTagName = NewName = NewBody = "";
                NewIsDraft = NewIsPrerelease = false;
                SelectedExistingTag = null;
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

        if (ErrorMessage is null)
            await RefreshList();
    }

    internal void OpenInBrowser(ReleaseRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.Url))
            _openUrl(row.Url);
    }

    // ---- Plumbing -----------------------------------------------------------------------------

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();
    partial void OnIsSupportedChanged(bool value) => NotifyCommandStates();
    partial void OnNewTagNameChanged(string value) => PublishCommand.NotifyCanExecuteChanged();

    private void NotifyCommandStates()
    {
        RefreshListCommand.NotifyCanExecuteChanged();
        BeginComposeCommand.NotifyCanExecuteChanged();
        GenerateNotesCommand.NotifyCanExecuteChanged();
        PublishCommand.NotifyCanExecuteChanged();
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

/// <summary>One release row (T-28): tag / name / Draft·Prerelease badges / published date / open-in-browser,
/// routed back through the parent.</summary>
public partial class ReleaseRowViewModel : ViewModelBase
{
    private readonly ReleasesViewModel _parent;

    public string TagName { get; }
    public string Name { get; }
    public string Body { get; }
    public bool IsDraft { get; }
    public bool IsPrerelease { get; }
    public string Author { get; }
    public DateTimeOffset? PublishedAt { get; }
    public string Url { get; }

    public ReleaseRowViewModel(ReleaseItem item, ReleasesViewModel parent)
    {
        _parent = parent;
        TagName = item.TagName;
        Name = string.IsNullOrEmpty(item.Name) ? item.TagName : item.Name;
        Body = item.Body;
        IsDraft = item.IsDraft;
        IsPrerelease = item.IsPrerelease;
        Author = item.Author;
        PublishedAt = item.PublishedAt;
        Url = item.Url;
    }

    public string TagText => string.IsNullOrEmpty(TagName) ? "" : TagName;
    public string AuthorText => string.IsNullOrEmpty(Author) ? "" : $"by {Author}";
    public bool HasUrl => !string.IsNullOrWhiteSpace(Url);
    public bool HasBody => !string.IsNullOrWhiteSpace(Body);
    public string PublishedText => PublishedAt is { } p
        ? $"published {p.LocalDateTime:MMM d, yyyy}"
        : (IsDraft ? "unpublished draft" : "");

    [RelayCommand]
    private void OpenInBrowser() => _parent.OpenInBrowser(this);
}
