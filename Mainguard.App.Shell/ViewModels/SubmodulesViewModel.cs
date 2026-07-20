using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Submodules-management panel (T-16): lists the superproject's submodules with their rolled-up
/// status and drives init/update, per-submodule update-to-remote, sync, and "open as its own
/// repo" through <see cref="IGitService"/>. Reads come from <c>repo.Submodules</c>; every mutation
/// is CLI-driven in Core. All git work runs off the UI thread; typed failures surface as
/// <see cref="ErrorMessage"/>. Hosted by SubmodulesWindow.
/// </summary>
public partial class SubmodulesViewModel : ViewModelBase
{
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly Action? _onChanged;
    // Opens a path as its own top-level repository (wired to MainWindowViewModel.OpenRepository).
    private readonly Action<string>? _openRepository;

    public ObservableCollection<SubmoduleRowViewModel> Submodules { get; } = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isEmpty;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public SubmodulesViewModel(IGitService git, string repoPath,
        Action? onChanged = null, Action<string>? openRepository = null)
    {
        _git = git;
        _repoPath = repoPath;
        _onChanged = onChanged;
        _openRepository = openRepository;
        Reload();
    }

    private void Reload()
    {
        Submodules.Clear();
        try
        {
            foreach (var sm in _git.GetSubmodules(_repoPath))
                Submodules.Add(new SubmoduleRowViewModel(sm, this));
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        IsEmpty = Submodules.Count == 0;
    }

    // Runs a git mutation off the UI thread, funnels any typed failure into ErrorMessage, and
    // reloads the list on success. Guards against overlapping runs via IsBusy.
    private async Task RunAsync(Action mutate)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await Task.Run(mutate);
            _onChanged?.Invoke();
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

    // Initialize + check out every submodule to the recorded commit (the fresh-clone setup action).
    [RelayCommand]
    private Task UpdateAll() => RunAsync(() => _git.UpdateSubmodules(_repoPath));

    // Re-sync each submodule's remote URL from .gitmodules into its own config.
    [RelayCommand]
    private Task Sync() => RunAsync(() => _git.SyncSubmodules(_repoPath));

    [RelayCommand]
    private void Refresh() => Reload();

    internal Task UpdateRemoteAsync(SubmoduleRowViewModel row)
        => RunAsync(() => _git.UpdateSubmoduleRemote(_repoPath, row.Path));

    // Opens the submodule's working directory as a first-class GitLoom repository.
    internal void OpenAsRepo(SubmoduleRowViewModel row)
    {
        var full = System.IO.Path.Combine(_repoPath, row.Path);
        _openRepository?.Invoke(full);
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One submodule row: its path/URL/short SHA + rolled-up state, and the per-row actions.</summary>
public partial class SubmoduleRowViewModel : ViewModelBase
{
    private readonly SubmodulesViewModel _parent;

    public string Path { get; }
    public string Url { get; }
    public SubmoduleState Status { get; }

    public SubmoduleRowViewModel(SubmoduleItem item, SubmodulesViewModel parent)
    {
        _parent = parent;
        Path = item.Path;
        Url = item.Url;
        Status = item.Status;
        ShortSha = string.IsNullOrEmpty(item.HeadSha) ? "—" : item.HeadSha[..Math.Min(7, item.HeadSha.Length)];
    }

    public string ShortSha { get; }

    /// <summary>Human-readable status label for the row badge.</summary>
    public string StatusLabel => Status switch
    {
        SubmoduleState.Uninitialized => "Not initialized",
        SubmoduleState.UpToDate => "Up to date",
        SubmoduleState.Modified => "Modified",
        SubmoduleState.Dirty => "Dirty",
        _ => Status.ToString()
    };

    // Drives the status pill's semantic class (see App.axaml). Never a raw color.
    public bool IsUninitialized => Status == SubmoduleState.Uninitialized;
    public bool IsUpToDate => Status == SubmoduleState.UpToDate;
    public bool IsModified => Status == SubmoduleState.Modified;
    public bool IsDirty => Status == SubmoduleState.Dirty;

    // "Open as its own repo" only makes sense once the submodule is actually checked out.
    public bool CanOpen => Status != SubmoduleState.Uninitialized;

    [RelayCommand]
    private Task UpdateRemote() => _parent.UpdateRemoteAsync(this);

    [RelayCommand]
    private void OpenAsRepo() => _parent.OpenAsRepo(this);
}
