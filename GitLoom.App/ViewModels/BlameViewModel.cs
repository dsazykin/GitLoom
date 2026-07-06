using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Drives the toggleable blame gutter (T-11): exposes the per-line <see cref="BlameLine"/>
/// model and the current file text to the view. Blame is computed off the UI thread on
/// <see cref="Task.Run"/> with a <see cref="CancellationToken"/> that is cancelled the moment a
/// newer file is requested, so rapid file switching can never render a stale gutter — the
/// invariant pinned by <c>BlameGutter_ShouldCancelStaleLoad_OnFileSwitch</c>. Blame never runs
/// in a property getter (a §T-11 rejection trigger).
/// </summary>
public partial class BlameViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;

    // T-32: commit-context service + routing sinks (null when blame-to-PR isn't wired). Optional so the
    // T-11 constructor keeps working; the gutter's "why behind this line" action is inert without them.
    private readonly ICommitContextService? _commitContext;
    private readonly Action<PullRequestItem>? _openPullRequest;
    private readonly Action<LinkedIssueRef>? _openLinkedIssue;

    // The token source of the in-flight load. Cancelled (and replaced) on every new load so a
    // superseded computation, when it finally returns, discards its result instead of rendering.
    private CancellationTokenSource? _cts;

    // Separate token source for the in-flight commit-context lookup (T-32) so it can be cancelled
    // independently of a blame reload.
    private CancellationTokenSource? _contextCts;

    [ObservableProperty]
    private bool _isBlameVisible;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private string _filePath = string.Empty;

    [ObservableProperty]
    private string _rawContent = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private IReadOnlyList<BlameLine> _blameLines = Array.Empty<BlameLine>();

    // ---- Commit context / blame → PR (T-32) ---------------------------------------------------

    /// <summary>The open "why behind this line" popover (T-32), or null when none is shown.</summary>
    [ObservableProperty]
    private BlameCommitContextViewModel? _lineContext;

    /// <summary>True while the commit-context lookup for a gutter click is in flight (gates the action + shows a spinner).</summary>
    [ObservableProperty]
    private bool _isContextBusy;

    /// <summary>True when the origin host has a blame-to-PR provider and a token — the gutter's jump action is offered.</summary>
    public bool IsCommitContextSupported { get; }

    public bool HasFile => !string.IsNullOrEmpty(FilePath);

    /// <summary>Raised when a gutter row is clicked so the host can select that commit in the
    /// timeline. Wired at the UI layer (deferred blame-gutter polish).</summary>
    public event Action<string>? CommitSelected;

    /// <summary>Fires with the file path each time a NON-stale blame result is applied (a superseded,
    /// cancelled load never fires it). The host can react to completion; the cancellation test asserts a
    /// stale load never rendered.</summary>
    public event Action<string>? BlameApplied;

    public BlameViewModel(IGitService gitService, string repoPath,
        ICommitContextService? commitContext = null,
        Action<PullRequestItem>? openPullRequest = null,
        Action<LinkedIssueRef>? openLinkedIssue = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _commitContext = commitContext;
        _openPullRequest = openPullRequest;
        _openLinkedIssue = openLinkedIssue;
        IsCommitContextSupported = SafeIsContextSupported();
    }

    private bool SafeIsContextSupported()
    {
        try { return _commitContext?.IsSupported(_repoPath) ?? false; }
        catch { return false; }
    }

    /// <summary>Toggles the gutter. Turning it on loads blame for the current file; turning it off
    /// cancels any in-flight load and clears the rows.</summary>
    [RelayCommand]
    private async Task ToggleBlame()
    {
        IsBlameVisible = !IsBlameVisible;
        if (IsBlameVisible)
        {
            await LoadAsync(FilePath);
        }
        else
        {
            _cts?.Cancel();
            ClearRows();
            IsLoading = false;
        }
    }

    /// <summary>Points the gutter at a new file. Recomputes only while the gutter is visible;
    /// otherwise just records the path so a later toggle picks it up.</summary>
    public Task SetFileAsync(string? path)
    {
        FilePath = path ?? string.Empty;
        if (IsBlameVisible)
        {
            return LoadAsync(FilePath);
        }
        ClearRows();
        return Task.CompletedTask;
    }

    /// <summary>Selects the commit behind a gutter row (click → timeline selection).</summary>
    public void SelectCommit(string sha)
    {
        if (!string.IsNullOrEmpty(sha)) CommitSelected?.Invoke(sha);
    }

    // ---- Commit context / blame → PR (T-32) ---------------------------------------------------

    /// <summary>
    /// Resolves the "why behind this line" for a gutter commit (T-32): the PR(s) that contain it and the
    /// issues they link, off the UI thread and gated by <see cref="IsContextBusy"/>. Opens the popover on
    /// success; a typed host failure surfaces in <see cref="ErrorMessage"/>. Inert when unsupported.
    /// </summary>
    public async Task ShowCommitContextAsync(string sha)
    {
        if (_commitContext is null || !IsCommitContextSupported || string.IsNullOrEmpty(sha) || IsContextBusy)
            return;

        _contextCts?.Cancel();
        var cts = new CancellationTokenSource();
        _contextCts = cts;
        var token = cts.Token;

        LineContext = null;
        IsContextBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await Task.Run(() => _commitContext.GetForCommitAsync(_repoPath, sha, token), token);
            if (token.IsCancellationRequested) return;   // superseded by a newer click
            LineContext = new BlameCommitContextViewModel(result, OpenPullRequest, OpenLinkedIssue);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before/while running — nothing to show.
        }
        catch (GitLoomException ex)
        {
            if (!token.IsCancellationRequested) ErrorMessage = ex.Message;
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) ErrorMessage = ex.Message;
        }
        finally
        {
            if (!token.IsCancellationRequested) IsContextBusy = false;
        }
    }

    /// <summary>Closes the commit-context popover.</summary>
    [RelayCommand]
    public void CloseLineContext() => LineContext = null;

    // Routes a PR jump: the host-supplied sink (into the PR panel) when wired, else opens the PR in a browser.
    private void OpenPullRequest(PullRequestItem pr)
    {
        if (_openPullRequest is not null) _openPullRequest(pr);
        else if (!string.IsNullOrWhiteSpace(pr.Url)) OpenUrlInBrowser(pr.Url);
    }

    // Routes an issue jump: the host-supplied sink (into the Issues panel) when wired, else opens the
    // issue in a browser. The browser fallback builds the GitHub web URL from the parsed owner/repo +
    // number (GitHub is the only implemented host today).
    private void OpenLinkedIssue(LinkedIssueRef issue)
    {
        if (_openLinkedIssue is not null) { _openLinkedIssue(issue); return; }
        if (!string.IsNullOrWhiteSpace(issue.RepoFullName))
            OpenUrlInBrowser($"https://github.com/{issue.RepoFullName}/issues/{issue.Number}");
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
            // Opening a browser is best-effort; never crash the gutter over it.
        }
    }

    /// <summary>
    /// Loads blame for <paramref name="path"/> off the UI thread, cancelling any prior load. If a
    /// newer load supersedes this one, its result is discarded rather than applied.
    /// </summary>
    public async Task LoadAsync(string? path)
    {
        _cts?.Cancel();

        if (string.IsNullOrEmpty(path))
        {
            ClearRows();
            IsLoading = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;

        FilePath = path;
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var result = await Task.Run(() => _gitService.GetBlame(_repoPath, path, null), token);
            if (token.IsCancellationRequested) return;   // a newer file switch superseded us
            ApplyResult(path, result);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before the work started — nothing to apply.
        }
        catch (GitLoomException ex)
        {
            if (token.IsCancellationRequested) return;
            ErrorMessage = ex.Message;
            ClearRows();
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            ErrorMessage = ex.Message;
            ClearRows();
        }
        finally
        {
            if (!token.IsCancellationRequested) IsLoading = false;
        }
    }

    private void ApplyResult(string path, IReadOnlyList<BlameLine> result)
    {
        BlameLines = result;
        RawContent = TryReadWorkingFile(path);
        IsLoading = false;
        BlameApplied?.Invoke(path);
    }

    private void ClearRows()
    {
        BlameLines = Array.Empty<BlameLine>();
        RawContent = string.Empty;
    }

    // The gutter aligns against the committed (HEAD) blame; the editor shows the working file,
    // which matches when the tree is clean. Reading is best-effort — a missing file just yields
    // an empty editor rather than an error.
    private string TryReadWorkingFile(string path)
    {
        try
        {
            var full = System.IO.Path.Combine(_repoPath, path);
            return System.IO.File.Exists(full) ? System.IO.File.ReadAllText(full) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
