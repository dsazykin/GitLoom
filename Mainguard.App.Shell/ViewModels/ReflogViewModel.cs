using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Services;
using Mainguard.Git.Models;
using Mainguard.Git.Services;
using LibGit2Sharp;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Reflog viewer &amp; recovery panel (T-20): lists a ref's reflog entries newest-first (each move's
/// from→to sha, message, when) and drives the two recovery actions per entry:
/// <list type="bullet">
///   <item><b>Restore</b> — a hard reset of the current branch to that entry's target, gated by
///     <see cref="IConfirmationService"/> (destructive) and journaled via <c>ResetToCommit</c>, so it
///     is itself undoable through the T-19 operation history.</item>
///   <item><b>Create branch here</b> — recovers an orphaned tip (deleted-branch recovery) by branching
///     at the entry's target via <c>CreateBranchAt</c>, also journaled.</item>
/// </list>
/// A ref picker (HEAD + local branches) reloads the list per ref. All git work runs off the UI thread;
/// typed failures surface as <see cref="ErrorMessage"/>. Hosted by ReflogWindow.
/// </summary>
public partial class ReflogViewModel : ViewModelBase
{
    private readonly IGitService _git;
    private readonly string _repoPath;
    private readonly IConfirmationService _confirm;
    private readonly Action? _onChanged;

    /// <summary>Ref names offered in the picker: "HEAD" first, then each local branch (T-20 "list per ref").</summary>
    public ObservableCollection<string> RefNames { get; } = new();

    public ObservableCollection<ReflogRowViewModel> Entries { get; } = new();

    [ObservableProperty]
    private string _selectedRef = "HEAD";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isEmpty;

    public bool HasEntries => Entries.Count > 0;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public ReflogViewModel(IGitService git, string repoPath,
        IConfirmationService? confirm = null, Action? onChanged = null, string initialRef = "HEAD")
    {
        _git = git;
        _repoPath = repoPath;
        _confirm = confirm ?? new DialogConfirmationService();
        _onChanged = onChanged;

        RefNames.Add("HEAD");
        try
        {
            foreach (var b in _git.GetBranches(_repoPath).Where(b => !b.IsRemote))
                if (!RefNames.Contains(b.FriendlyName))
                    RefNames.Add(b.FriendlyName);
        }
        catch
        {
            // A ref-picker that can't enumerate branches still works for HEAD; don't block the panel.
        }

        _selectedRef = RefNames.Contains(initialRef) ? initialRef : "HEAD";
        Reload();
    }

    partial void OnSelectedRefChanged(string value) => Reload();

    private void Reload()
    {
        Entries.Clear();
        try
        {
            foreach (var item in _git.GetReflog(_repoPath, SelectedRef))
                Entries.Add(new ReflogRowViewModel(item, this));
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        IsEmpty = Entries.Count == 0;
        OnPropertyChanged(nameof(HasEntries));
    }

    [RelayCommand]
    private void Refresh() => Reload();

    // Restore = hard-reset the working branch to the entry's target. Destructive → confirm first;
    // routed through the journaled GitService.ResetToCommit so it lands in the T-19 undo history.
    internal async Task RestoreAsync(ReflogRowViewModel row)
    {
        if (IsBusy) return;
        var confirmed = await _confirm.ConfirmAsync(
            "Restore to reflog entry",
            $"Hard-reset the current branch to {row.ShortToSha} ({row.Message})?\n\n" +
            "Uncommitted changes to tracked files will be lost. This is journaled, so you can undo it from Operation History.",
            "Restore");
        if (!confirmed) return;

        await RunAsync(() => _git.ResetToCommit(_repoPath, row.ToSha, ResetMode.Hard));
    }

    // Create branch here = recover an orphaned/deleted tip by branching at the entry's target.
    // Journaled via GitService.CreateBranchAt (undoable).
    internal async Task CreateBranchHereAsync(ReflogRowViewModel row)
    {
        if (IsBusy) return;
        var name = row.NewBranchName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            ErrorMessage = "Enter a name for the recovered branch.";
            return;
        }

        await RunAsync(() => _git.CreateBranchAt(_repoPath, name, row.ToSha, checkout: false));
        if (ErrorMessage is null)
        {
            row.NewBranchName = string.Empty;
            row.IsCreatingBranch = false;
        }
    }

    // Runs a git mutation off the UI thread, funnels typed failures into ErrorMessage, refreshes the
    // workspace on success, and reloads the list (the reflog itself grew a new entry). Guards overlap.
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

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One reflog entry row: from→to move, message, when, plus the restore / create-branch-here affordances.</summary>
public partial class ReflogRowViewModel : ViewModelBase
{
    private readonly ReflogViewModel _parent;

    public string FromSha { get; }
    public string ToSha { get; }
    public string Message { get; }
    public string When { get; }

    // The all-zero SHA marks the ref's very first entry (creation) — there is no meaningful "from".
    private static bool IsZero(string sha) => string.IsNullOrEmpty(sha) || sha.All(c => c == '0');

    public ReflogRowViewModel(ReflogItem item, ReflogViewModel parent)
    {
        _parent = parent;
        FromSha = item.FromSha;
        ToSha = item.ToSha;
        Message = string.IsNullOrEmpty(item.Message) ? "(no message)" : item.Message;
        When = item.When == default ? "" : item.When.ToLocalTime().ToString("MMM d, HH:mm");
    }

    public string ShortToSha => Short(ToSha);
    public string ShortFromSha => IsZero(FromSha) ? "—" : Short(FromSha);
    public string MoveText => $"{ShortFromSha} → {ShortToSha}";

    // Inline "create branch here" editing state for the row.
    [ObservableProperty]
    private bool _isCreatingBranch;

    [ObservableProperty]
    private string _newBranchName = string.Empty;

    [RelayCommand]
    private void BeginCreateBranch() => IsCreatingBranch = true;

    [RelayCommand]
    private void CancelCreateBranch()
    {
        IsCreatingBranch = false;
        NewBranchName = string.Empty;
    }

    [RelayCommand]
    private Task Restore() => _parent.RestoreAsync(this);

    [RelayCommand]
    private Task CreateBranchHere() => _parent.CreateBranchHereAsync(this);

    private static string Short(string sha)
        => string.IsNullOrEmpty(sha) || sha.Length <= 7 ? sha : sha.Substring(0, 7);
}
