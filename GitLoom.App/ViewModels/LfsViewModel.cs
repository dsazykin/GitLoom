using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Services;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Git LFS panel (T-17): the per-repo enable toggle, the tracked patterns (track/untrack), the list
/// of LFS objects with their downloaded/pointer status, and pull / prune actions. LFS is entirely a
/// CLI concern in Core (<see cref="ILfsService"/>); every git call runs off the UI thread and typed
/// failures surface as <see cref="ErrorMessage"/>. Prune shows the dry-run result and confirms
/// through <see cref="IConfirmationService"/> before the real prune. Hosted by LfsWindow.
/// </summary>
public partial class LfsViewModel : ViewModelBase
{
    private readonly ILfsService _lfs;
    private readonly string _repoPath;
    private readonly IConfirmationService _confirm;

    public ObservableCollection<LfsPatternRowViewModel> Patterns { get; } = new();
    public ObservableCollection<LfsFileRowViewModel> Files { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    // A neutral status line (e.g. the prune dry-run summary).
    [ObservableProperty]
    private string? _statusMessage;

    // False → git-lfs is not installed on this machine; the panel shows a notice and disables actions.
    [ObservableProperty]
    private bool _isAvailable;

    // The per-repo enable toggle (git lfs install/uninstall --local).
    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private string _newPattern = string.Empty;

    [ObservableProperty]
    private bool _hasNoPatterns;

    [ObservableProperty]
    private bool _hasNoFiles;

    // Guards the enable toggle so Reload() setting IsEnabled doesn't re-trigger install/uninstall.
    private bool _suppressToggle;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public LfsViewModel(ILfsService lfs, string repoPath, IConfirmationService? confirm = null)
    {
        _lfs = lfs;
        _repoPath = repoPath;
        _confirm = confirm ?? new DialogConfirmationService();
        Reload();
    }

    private void Reload()
    {
        Patterns.Clear();
        Files.Clear();
        try
        {
            IsAvailable = _lfs.IsAvailable(_repoPath);
            if (!IsAvailable)
            {
                SetEnabledQuiet(false);
                ErrorMessage = null;
                HasNoPatterns = true;
                HasNoFiles = true;
                return;
            }

            SetEnabledQuiet(_lfs.IsEnabledForRepo(_repoPath));

            foreach (var pattern in _lfs.ListTrackedPatterns(_repoPath))
                Patterns.Add(new LfsPatternRowViewModel(pattern, this));
            foreach (var file in _lfs.ListLfsFiles(_repoPath))
                Files.Add(new LfsFileRowViewModel(file));

            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        HasNoPatterns = Patterns.Count == 0;
        HasNoFiles = Files.Count == 0;
    }

    private void SetEnabledQuiet(bool value)
    {
        _suppressToggle = true;
        IsEnabled = value;
        _suppressToggle = false;
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppressToggle) return;
        _ = RunAsync(() =>
        {
            if (value) _lfs.Install(_repoPath);
            else _lfs.Uninstall(_repoPath);
        });
    }

    // Runs a git mutation off the UI thread, funnels typed failures into ErrorMessage, and reloads.
    private async Task RunAsync(Action mutate)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await Task.Run(mutate);
            Reload();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Track()
    {
        var pattern = NewPattern?.Trim();
        if (string.IsNullOrEmpty(pattern)) return;
        await RunAsync(() => _lfs.Track(_repoPath, pattern));
        NewPattern = string.Empty;
    }

    internal Task UntrackAsync(string pattern) => RunAsync(() => _lfs.Untrack(_repoPath, pattern));

    [RelayCommand]
    private Task Pull() => RunAsync(() => _lfs.Pull(_repoPath));

    // Prune: preview with --dry-run, confirm, then run for real (destructive of local objects only).
    [RelayCommand]
    private async Task Prune()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;
        try
        {
            var preview = await Task.Run(() => _lfs.Prune(_repoPath, dryRun: true));
            StatusMessage = preview;

            var confirmed = await _confirm.ConfirmAsync(
                "Prune LFS objects",
                $"{preview}\n\nRemove these old local LFS objects? Remote copies are unaffected and can be re-fetched.",
                "Prune");
            if (!confirmed) return;

            StatusMessage = await Task.Run(() => _lfs.Prune(_repoPath, dryRun: false));
            Reload();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Refresh() => Reload();

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One tracked-pattern row (e.g. <c>*.bin</c>) with its untrack action.</summary>
public partial class LfsPatternRowViewModel : ViewModelBase
{
    private readonly LfsViewModel _parent;

    public string Pattern { get; }

    public LfsPatternRowViewModel(string pattern, LfsViewModel parent)
    {
        Pattern = pattern;
        _parent = parent;
    }

    [RelayCommand]
    private Task Untrack() => _parent.UntrackAsync(Pattern);
}

/// <summary>One LFS object row: path, short OID, and whether the content is downloaded or a pointer.</summary>
public partial class LfsFileRowViewModel : ViewModelBase
{
    public string Path { get; }
    public string ShortOid { get; }
    public bool IsDownloaded { get; }

    public LfsFileRowViewModel(LfsFile file)
    {
        Path = file.Path;
        ShortOid = file.Oid.Length > 10 ? file.Oid[..10] : file.Oid;
        IsDownloaded = file.IsDownloaded;
    }

    public string StatusLabel => IsDownloaded ? "Downloaded" : "Pointer";
    public bool IsPointer => !IsDownloaded;
}
