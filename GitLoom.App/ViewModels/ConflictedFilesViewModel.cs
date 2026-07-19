using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Exceptions;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using LibGit2Sharp;

namespace GitLoom.App.ViewModels;

public partial class ConflictedFileItem : ObservableObject
{
    public string Path { get; }
    public bool HasBase { get; }
    public bool HasOurs { get; }
    public bool HasTheirs { get; }

    public ConflictedFileItem(ConflictedFile model)
    {
        Path = model.Path;
        HasBase = model.HasBase;
        HasOurs = model.HasOurs;
        HasTheirs = model.HasTheirs;
    }

    public bool CanUseOurs => HasOurs;
    public bool CanUseTheirs => HasTheirs;
    public string StateNote => !HasOurs ? "(deleted on our side)"
                             : !HasTheirs ? "(deleted on their side)"
                             : "";
}

// Service-driven conflict list with session-completion gating (T-04). Every git
// action runs off the UI thread; completion is un-executable while any conflict
// remains. The old raw-Process RunGit is gone — actions call service methods.
public partial class ConflictedFilesViewModel : ObservableObject
{
    private readonly string _repoPath = null!;
    private readonly IGitService _git = null!;
    private readonly IMergeDiffService _merge = null!;
    private readonly Window _window = null!;
    private readonly CurrentOperation _operation;
    private int _total;

    [ObservableProperty] private ObservableCollection<ConflictedFileItem> _files = new();
    [ObservableProperty] private bool _isBusy;

    // Design-time only.
    public ConflictedFilesViewModel() { }

    public ConflictedFilesViewModel(string repoPath, IGitService git, IMergeDiffService merge, Window window)
    {
        _repoPath = repoPath;
        _git = git;
        _merge = merge;
        _window = window;
        _operation = git.GetCurrentOperation(repoPath);
        Reload(initial: true);
    }

    public bool ShowContinueRebase =>
        _operation is CurrentOperation.Rebase or CurrentOperation.RebaseInteractive
                   or CurrentOperation.RebaseMerge or CurrentOperation.ApplyMailboxOrRebase;
    public bool ShowCommitMerge => !ShowContinueRebase;
    public string CancelButtonText => ShowContinueRebase ? "Abort Rebase" : "Abort Merge";

    public string HeaderText => $"{_total - Files.Count} of {_total} resolved";

    private void Reload(bool initial = false)
    {
        var conflicts = _git.GetConflicts(_repoPath);
        Files.Clear();
        foreach (var c in conflicts)
            Files.Add(new ConflictedFileItem(c));
        if (initial) _total = Files.Count;
        OnPropertyChanged(nameof(HeaderText));
        CommitMergeCommand.NotifyCanExecuteChanged();
        ContinueRebaseCommand.NotifyCanExecuteChanged();
    }

    private bool CanRun() => !IsBusy;
    private bool CanComplete() => !IsBusy && !_git.HasUnresolvedConflicts(_repoPath);

    partial void OnIsBusyChanged(bool value)
    {
        ResolveOursCommand.NotifyCanExecuteChanged();
        ResolveTheirsCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        CommitMergeCommand.NotifyCanExecuteChanged();
        ContinueRebaseCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ResolveOurs(ConflictedFileItem item) => await ResolveWithSideAsync(item, ConflictSide.Ours);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ResolveTheirs(ConflictedFileItem item) => await ResolveWithSideAsync(item, ConflictSide.Theirs);

    private async Task ResolveWithSideAsync(ConflictedFileItem item, ConflictSide side)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _git.ResolveFileWithSide(_repoPath, item.Path, side));
            Reload();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Edit(ConflictedFileItem item)
    {
        var dialog = new Views.ConflictResolverWindow();
        var vm = new ConflictResolverWindowViewModel(
            _git, _merge, _repoPath, item.Path, item.HasOurs, item.HasTheirs)
        { Window = dialog };
        dialog.DataContext = vm;
        var result = await dialog.ShowDialog<bool>(_window);
        if (result) Reload();
    }

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task CommitMerge()
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _git.Commit(_repoPath, _git.GetMergeMessage(_repoPath)));
            _window.Close(true);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanComplete))]
    private async Task ContinueRebase()
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => _git.ContinueRebase(_repoPath));
            _window.Close(true);
        }
        catch (MergeConflictException)
        {
            // The rebase advanced to the next conflicted commit — reload and keep going.
            Reload();
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Cancel()
    {
        IsBusy = true;
        try
        {
            await Task.Run(() =>
            {
                if (ShowContinueRebase) _git.AbortRebase(_repoPath);
                else _git.AbortMerge(_repoPath);
            });
            _window.Close(false);
        }
        finally { IsBusy = false; }
    }
}
