using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Controls;
using GitLoom.Core.Graph;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public class FileItemViewModel
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
}

public class FileGroupViewModel
{
    public string GroupName { get; set; } = string.Empty;
    public ObservableCollection<FileItemViewModel> Files { get; set; } = new();
}

public partial class RepoTreeNodeViewModel : ViewModelBase
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public ObservableCollection<RepoTreeNodeViewModel> Children { get; } = new();

    [ObservableProperty]
    private bool? _isChecked = false;

    partial void OnIsCheckedChanged(bool? value)
    {
        if (value.HasValue)
        {
            foreach (var child in Children)
            {
                child.IsChecked = value;
            }
        }
    }
}

public partial class CommitTimelineViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;
    private readonly GitLoom.Core.Services.ISettingsService _settingsService;

    [ObservableProperty]
    private ObservableCollection<CommitRowViewModel> _commits = new();

    [ObservableProperty]
    private CommitSearchFilter _searchFilter = new();

    [ObservableProperty]
    private string? _searchText;

    partial void OnSearchTextChanged(string? value)
    {
        SearchFilter.Text = value;
        LoadInitialCommits();
    }

    [ObservableProperty]
    private ObservableCollection<string> _authors = new();

    [ObservableProperty]
    private string? _selectedAuthor;

    partial void OnSelectedAuthorChanged(string? value)
    {
        SearchFilter.Author = value;
        LoadInitialCommits();
    }

    [ObservableProperty]
    private ObservableCollection<string> _dateFilters = new() { "Any", "Last 24 Hours", "Today", "Last 7 Days", "Last 30 Days" };

    [ObservableProperty]
    private string _selectedDateFilter = "Any";

    partial void OnSelectedDateFilterChanged(string value)
    {
        var now = DateTime.Now;
        SearchFilter.DateFrom = value switch
        {
            "Last 24 Hours" => now.AddHours(-24),
            "Today" => now.Date,
            "Last 7 Days" => now.Date.AddDays(-7),
            "Last 30 Days" => now.Date.AddDays(-30),
            _ => null
        };
        LoadInitialCommits();
    }

    [ObservableProperty]
    private ObservableCollection<GitBranchItem> _branches = new();

    [ObservableProperty]
    private GitBranchItem? _selectedBranchItem;

    partial void OnSelectedBranchItemChanged(GitBranchItem? value)
    {
        SearchFilter.BranchName = value?.Name;
        LoadInitialCommits();
    }

    [ObservableProperty]
    private ObservableCollection<RepoTreeNodeViewModel> _repoTree = new();

    [ObservableProperty]
    private string? _customPathsText;

    [RelayCommand]
    private void ApplyPathsFilter()
    {
        var manualPaths = string.IsNullOrWhiteSpace(CustomPathsText)
            ? new List<string>()
            : CustomPathsText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();

        var treePaths = new List<string>();
        void Traverse(RepoTreeNodeViewModel node)
        {
            if (!node.IsDirectory && node.IsChecked == true) treePaths.Add(node.FullPath);
            foreach (var child in node.Children) Traverse(child);
        }
        foreach (var root in RepoTree) Traverse(root);

        SearchFilter.FilePaths = manualPaths.Concat(treePaths).Distinct().ToList();
        if (SearchFilter.FilePaths.Count == 0) SearchFilter.FilePaths = null;

        LoadInitialCommits();
    }

    [ObservableProperty]
    private bool _isRefreshing;

    [RelayCommand]
    private async System.Threading.Tasks.Task RefreshCommitsAsync()
    {
        if (IsRefreshing) return;
        IsRefreshing = true;
        var minTask = System.Threading.Tasks.Task.Delay(1000);
        LoadInitialCommits();
        await minTask;
        IsRefreshing = false;
    }

    [RelayCommand]
    private void EnableGitLogIndexing()
    {
        // Mock implementation for indexing
        System.Diagnostics.Debug.WriteLine("Enable Git Log Indexing toggled");
    }

    [RelayCommand]
    private System.Threading.Tasks.Task CherryPickCommit(string sha)
        => RunGitActionAsync(() => _gitService.CherryPick(_repoPath, sha));

    // Toggle Properties for View options
    [ObservableProperty] private bool _referencesOnTheLeft = true;
    partial void OnReferencesOnTheLeftChanged(bool value) => _settingsService.Update(p => p.ReferencesOnTheLeft = value);

    [ObservableProperty] private bool _showAuthorColumn = true;
    partial void OnShowAuthorColumnChanged(bool value) => _settingsService.Update(p => p.ShowAuthorColumn = value);

    [ObservableProperty] private bool _showDateColumn = true;
    partial void OnShowDateColumnChanged(bool value) => _settingsService.Update(p => p.ShowDateColumn = value);

    [ObservableProperty] private bool _showHashColumn = true;
    partial void OnShowHashColumnChanged(bool value) => _settingsService.Update(p => p.ShowHashColumn = value);

    [ObservableProperty] private bool _compactReferencesView = true;
    partial void OnCompactReferencesViewChanged(bool value) => _settingsService.Update(p => p.CompactReferencesView = value);

    [ObservableProperty] private bool _tagNames;
    partial void OnTagNamesChanged(bool value) => _settingsService.Update(p => p.TagNames = value);

    [ObservableProperty] private bool _longEdges;
    partial void OnLongEdgesChanged(bool value) => _settingsService.Update(p => p.LongEdges = value);

    [ObservableProperty] private bool _commitTimestamp;
    partial void OnCommitTimestampChanged(bool value) => _settingsService.Update(p => p.CommitTimestamp = value);

    // Toggle Properties for Highlight options
    [ObservableProperty] private bool _highlightMyCommits = true;
    partial void OnHighlightMyCommitsChanged(bool value) { _settingsService.Update(p => p.HighlightMyCommits = value); UpdateHighlights(); }

    [ObservableProperty] private bool _highlightMergeCommits;
    partial void OnHighlightMergeCommitsChanged(bool value) { _settingsService.Update(p => p.HighlightMergeCommits = value); UpdateHighlights(); }

    [ObservableProperty] private bool _highlightCurrentBranch = true;
    partial void OnHighlightCurrentBranchChanged(bool value) { _settingsService.Update(p => p.HighlightCurrentBranch = value); UpdateHighlights(); }

    [ObservableProperty] private bool _highlightNotCherryPickedCommits;
    partial void OnHighlightNotCherryPickedCommitsChanged(bool value) { _settingsService.Update(p => p.HighlightNotCherryPickedCommits = value); UpdateHighlights(); }

    private void UpdateHighlights()
    {
        string currentUser = Environment.UserName;
        foreach (var row in Commits)
        {
            bool hl = false;
            if (HighlightMergeCommits && row.Commit.ParentShas.Count > 1) hl = true;
            if (HighlightMyCommits && row.Commit.AuthorName.Contains(currentUser, StringComparison.OrdinalIgnoreCase)) hl = true;

            // Highlight current branch if we have branch data (simplified)
            if (HighlightCurrentBranch && row.Node.LaneIndex == 0) hl = true; // Assuming main branch is lane 0

            row.IsHighlighted = hl;
        }
    }

    [ObservableProperty]
    private BranchBrowserViewModel _branchBrowser;

    [ObservableProperty]
    private CommitRowViewModel? _selectedCommit;

    [ObservableProperty]
    private ObservableCollection<FileGroupViewModel> _selectedCommitFiles = new();

    [ObservableProperty]
    private ObservableCollection<string> _selectedCommitBranches = new();

    [ObservableProperty]
    private ObservableCollection<string> _selectedCommitTags = new();

    [ObservableProperty]
    private int _selectedCommitFileCount;

    private MenuItemViewModel? _selectedBranchMenuItem;
    public MenuItemViewModel? SelectedBranchMenuItem
    {
        get => _selectedBranchMenuItem;
        set
        {
            if (SetProperty(ref _selectedBranchMenuItem, value) && value != null)
            {
                LoadInitialCommits(value.Header);
            }
        }
    }

    partial void OnSelectedCommitChanged(CommitRowViewModel? value)
    {
        if (value != null)
        {
            FetchCommitDetailsAsync(value.Commit.Sha);
        }
    }

    private async void FetchCommitDetailsAsync(string sha)
    {
        SelectedCommitFiles.Clear();
        SelectedCommitBranches.Clear();
        SelectedCommitTags.Clear();
        SelectedCommitFileCount = 0;

        var (files, branches, tags) = await System.Threading.Tasks.Task.Run(() =>
        {
            var f = _gitService.GetCommitModifiedFiles(_repoPath, sha).ToList();
            var b = _gitService.GetBranchesContainingCommit(_repoPath, sha).ToList();
            // Tags whose peeled target is exactly this commit (chips joined by SHA).
            var t = _gitService.GetTags(_repoPath).Where(x => x.TargetSha == sha).Select(x => x.Name).ToList();
            return (f, b, t);
        });

        SelectedCommitFileCount = files.Count;
        var groups = files.GroupBy(f =>
        {
            var idx = f.IndexOf('/');
            return idx > 0 ? f.Substring(0, idx) : "Root";
        }).OrderBy(g => g.Key);

        foreach (var g in groups)
        {
            var fileItems = g.Select(f => new FileItemViewModel
            {
                FilePath = f,
                FileName = System.IO.Path.GetFileName(f)
            });

            SelectedCommitFiles.Add(new FileGroupViewModel
            {
                GroupName = $"{g.Key} {g.Count()} files",
                Files = new ObservableCollection<FileItemViewModel>(fileItems)
            });
        }

        foreach (var b in branches)
        {
            SelectedCommitBranches.Add(b);
        }

        foreach (var t in tags)
        {
            SelectedCommitTags.Add(t);
        }
    }

    private readonly CommitGraphRouter _graphRouter = new();
    private GraphFringeState _currentFringe = new();

    private int _currentCommitSkip = 0;
    private const int CommitsChunkSize = 50;

    private readonly Action<string, bool>? _showNotificationAction;
    private readonly GitLoom.App.Services.IConfirmationService _confirmationService;
    private readonly IPinnedRefService _pinnedRefService;

    // Cached pinned-ref tip SHAs (pin order) fed to the router so pinned refs get left-most lanes.
    private IReadOnlyList<string> _priorityTips = Array.Empty<string>();

    [ObservableProperty]
    private bool _isBusy;

    // The ref label currently selected in the graph — the Delete key acts on this (T-09 §3.5).
    [ObservableProperty]
    private string? _selectedRefName;

    [ObservableProperty]
    private bool _currentBranchOnly;

    partial void OnCurrentBranchOnlyChanged(bool value)
    {
        SearchFilter.CurrentBranchOnly = value;
        LoadInitialCommits();
    }

    public CommitTimelineViewModel(IGitService gitService, string repoPath, Action<string, bool>? showNotificationAction = null,
        GitLoom.App.Services.IConfirmationService? confirmationService = null,
        IPinnedRefService? pinnedRefService = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _showNotificationAction = showNotificationAction;
        _confirmationService = confirmationService ?? new GitLoom.App.Services.DialogConfirmationService();
        _pinnedRefService = pinnedRefService ?? new PinnedRefService();
        _settingsService = new GitLoom.Core.Services.SettingsService();

        var p = _settingsService.Current;
        _compactReferencesView = p.CompactReferencesView;
        _tagNames = p.TagNames;
        _longEdges = p.LongEdges;
        _commitTimestamp = p.CommitTimestamp;
        _referencesOnTheLeft = p.ReferencesOnTheLeft;

        _showAuthorColumn = p.ShowAuthorColumn;
        _showDateColumn = p.ShowDateColumn;
        _showHashColumn = p.ShowHashColumn;

        _highlightMyCommits = p.HighlightMyCommits;
        _highlightMergeCommits = p.HighlightMergeCommits;
        _highlightCurrentBranch = p.HighlightCurrentBranch;
        _highlightNotCherryPickedCommits = p.HighlightNotCherryPickedCommits;

        _branchBrowser = new BranchBrowserViewModel(_gitService, _repoPath);
        _branchBrowser.LoadBranches();

        LoadFilterData();
    }

    private void LoadFilterData()
    {
        var branches = _gitService.GetBranches(_repoPath);
        foreach (var b in branches) Branches.Add(b);

        var authors = _gitService.GetAuthors(_repoPath);
        foreach (var a in authors) Authors.Add(a);

        var paths = _gitService.GetRepositoryPaths(_repoPath);
        BuildRepoTree(paths);
    }

    private void BuildRepoTree(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            RepoTreeNodeViewModel? currentParent = null;
            string currentPath = "";

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                currentPath = string.IsNullOrEmpty(currentPath) ? part : $"{currentPath}/{part}";
                bool isFile = i == parts.Length - 1;

                ObservableCollection<RepoTreeNodeViewModel> childrenCollection = currentParent == null ? RepoTree : currentParent.Children;

                var node = childrenCollection.FirstOrDefault(n => n.Name == part);
                if (node == null)
                {
                    node = new RepoTreeNodeViewModel
                    {
                        Name = part,
                        FullPath = currentPath,
                        IsDirectory = !isFile
                    };
                    childrenCollection.Add(node);
                }

                currentParent = node;
            }
        }
    }

    public void LoadInitialCommits(string? filterBranchName = null, string? filterFilePath = null)
    {
        if (filterBranchName != null) SearchFilter.BranchName = filterBranchName;
        // Support the legacy load initial commits by setting the paths array if single path is given
        if (filterFilePath != null) SearchFilter.FilePaths = new List<string> { filterFilePath };

        Commits.Clear();
        _currentCommitSkip = 0;
        _currentFringe = new GraphFringeState();
        _priorityTips = ComputePriorityTips();
        LoadMoreCommits();
    }

    // Resolves pinned ref names to their tip SHAs (in pin order). Branch tips win over tags; refs
    // that no longer resolve are skipped. Empty when nothing is pinned → the router is untouched.
    private IReadOnlyList<string> ComputePriorityTips()
    {
        var pinned = _pinnedRefService.GetPinnedRefs(_repoPath);
        if (pinned.Count == 0) return Array.Empty<string>();

        var branches = _gitService.GetBranches(_repoPath).ToList();
        var tags = _gitService.GetTags(_repoPath).ToList();
        var tips = new List<string>();
        foreach (var pin in pinned)
        {
            var branch = branches.FirstOrDefault(b => b.Name == pin.RefName || b.FriendlyName == pin.RefName);
            if (branch != null && !string.IsNullOrEmpty(branch.TipSha))
            {
                tips.Add(branch.TipSha);
                continue;
            }
            var tag = tags.FirstOrDefault(t => t.Name == pin.RefName);
            if (tag != null && !string.IsNullOrEmpty(tag.TargetSha))
            {
                tips.Add(tag.TargetSha);
            }
        }
        return tips;
    }

    [RelayCommand]
    public void LoadMoreCommits()
    {
        var nextChunk = _gitService.GetRecentCommits(_repoPath, _currentCommitSkip, CommitsChunkSize, SearchFilter).ToList();
        if (nextChunk.Count == 0) return;

        var routeResult = _graphRouter.RouteCommits(nextChunk, _currentFringe, _priorityTips);

        for (int i = 0; i < nextChunk.Count; i++)
        {
            Commits.Add(new CommitRowViewModel
            {
                Commit = nextChunk[i],
                Node = routeResult.Nodes[i]
            });
        }

        _currentFringe = routeResult.EndFringe;
        _currentCommitSkip += CommitsChunkSize;

        UpdateHighlights();
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CopyCommitHash(string sha)
    {
        var app = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var clipboard = app?.MainWindow?.Clipboard;
        if (clipboard != null) await clipboard.SetTextAsync(sha);
    }

    [RelayCommand]
    private System.Threading.Tasks.Task CheckoutRevision(string sha)
        => RunGitActionAsync(() => _gitService.CheckoutRevision(_repoPath, sha));

    [RelayCommand]
    private async System.Threading.Tasks.Task DiffWorkingTreeAgainstCommit(string sha)
    {
        try
        {
            // Keep the diff read (and its temp-file write) off the UI thread.
            var diff = await System.Threading.Tasks.Task.Run(() => _gitService.GetDiffAgainstCommit(_repoPath, sha)); // whole tree (filePath null)
            if (string.IsNullOrWhiteSpace(diff))
            {
                _showNotificationAction?.Invoke("No differences between the working tree and this commit.", false);
                return;
            }

            var shortSha = sha.Length >= 7 ? sha.Substring(0, 7) : sha;
            var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"GitLoom_worktree_vs_{shortSha}.patch");
            await System.Threading.Tasks.Task.Run(() => System.IO.File.WriteAllText(tempFile, diff));
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempFile) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            _showNotificationAction?.Invoke($"Failed to generate diff: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CreateTag(string sha)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new CreateTagDialogViewModel { TargetSha = sha };
            var dialog = new Views.CreateTagDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);

            if (vm.IsConfirmed)
            {
                try
                {
                    _gitService.CreateTag(_repoPath, vm.TagName, sha, vm.IsAnnotated ? vm.Message : null);
                    LoadInitialCommits();
                    BranchBrowser.LoadBranches();
                    if (SelectedCommit?.Commit.Sha == sha) FetchCommitDetailsAsync(sha);
                    _showNotificationAction?.Invoke($"Created tag '{vm.TagName}'.", false);
                }
                catch (Exception ex)
                {
                    _showNotificationAction?.Invoke(ex.Message, true);
                }
            }
        }
    }

    [RelayCommand]
    private System.Threading.Tasks.Task ResetCommitSoft(string sha)
        => RunGitActionAsync(() => _gitService.ResetToCommit(_repoPath, sha, LibGit2Sharp.ResetMode.Soft));

    [RelayCommand]
    private System.Threading.Tasks.Task ResetCommitMixed(string sha)
        => RunGitActionAsync(() => _gitService.ResetToCommit(_repoPath, sha, LibGit2Sharp.ResetMode.Mixed));

    // Hard reset is the one destructive graph action that MUST always confirm first
    // (T-09 invariant) — the confirmation is gated through IConfirmationService so it
    // is testable and can never be bypassed.
    [RelayCommand]
    private async System.Threading.Tasks.Task ResetCommitHard(string sha)
    {
        bool confirmed = await _confirmationService.ConfirmAsync(
            "Hard reset",
            $"Hard reset the current branch to {Shorten(sha)}?\nThis permanently discards all uncommitted changes and cannot be undone.",
            "Hard Reset");
        if (!confirmed) return;

        await RunGitActionAsync(() => _gitService.ResetToCommit(_repoPath, sha, LibGit2Sharp.ResetMode.Hard));
    }

    [RelayCommand]
    private System.Threading.Tasks.Task RevertCommit(string sha)
        => RunGitActionAsync(() => _gitService.RevertCommit(_repoPath, sha));

    private static string Shorten(string sha) => sha.Length >= 7 ? sha.Substring(0, 7) : sha;

    // Every graph menu action funnels through here: the LibGit2Sharp call runs off the
    // UI thread (never synchronously on it — a T-09 rejection trigger), IsBusy guards
    // against re-entrancy, and failures surface through the typed-exception hierarchy
    // instead of being swallowed.
    private async System.Threading.Tasks.Task RunGitActionAsync(Action gitAction)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(gitAction);
            LoadInitialCommits();
        }
        catch (GitLoom.Core.Exceptions.MergeConflictException ex)
        {
            _showNotificationAction?.Invoke(ex.Message, true);
        }
        catch (GitLoom.Core.Exceptions.GitLoomException ex)
        {
            _showNotificationAction?.Invoke(ex.Message, true);
        }
        catch (LibGit2Sharp.LibGit2SharpException ex)
        {
            _showNotificationAction?.Invoke(ex.Message, true);
        }
        catch (Exception ex)
        {
            _showNotificationAction?.Invoke(ex.Message, true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAmendCommit(string sha)
    {
        var branches = _gitService.GetBranches(_repoPath);
        var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
        return currentBranch != null && currentBranch.TipSha == sha;
    }

    [RelayCommand(CanExecute = nameof(CanAmendCommit))]
    private System.Threading.Tasks.Task AmendCommit(string sha)
    {
        // TODO: show input dialog for message. For now, append (amended).
        var commit = Commits.FirstOrDefault(c => c.Commit.Sha == sha);
        if (commit == null) return System.Threading.Tasks.Task.CompletedTask;

        var newMessage = commit.Commit.Message + " (amended)";
        return RunGitActionAsync(() => _gitService.AmendCommitMessage(_repoPath, sha, newMessage));
    }

    [RelayCommand]
    private void GoToParentCommit(string sha)
    {
        var current = Commits.FirstOrDefault(c => c.Commit.Sha == sha);
        if (current != null && current.Commit.ParentShas.Any())
        {
            var parentSha = current.Commit.ParentShas.First();
            var parent = Commits.FirstOrDefault(c => c.Commit.Sha == parentSha);
            if (parent != null) SelectedCommit = parent;
        }
    }

    [RelayCommand]
    private void GoToChildCommit(string sha)
    {
        var child = Commits.FirstOrDefault(c => c.Commit.ParentShas.Any(p => p == sha));
        if (child != null) SelectedCommit = child;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task InteractiveRebase(string baseSha)
    {
        try
        {
            var rebaseService = new GitLoom.Core.Services.InteractiveRebaseService();
            var vm = new InteractiveRebaseViewModel(rebaseService, _repoPath, baseSha, _showNotificationAction, _gitService);

            var app = Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
            if (app?.MainWindow == null) return;

            // Build the plan up front so problems (dirty tree, unborn HEAD, merge commit in
            // range, base not found) surface to the user instead of a window that never opens.
            vm.LoadPlan();

            var dialog = new GitLoom.App.Views.InteractiveRebaseWindow { DataContext = vm };
            await dialog.ShowDialog(app.MainWindow);
            LoadInitialCommits();
        }
        catch (System.Exception ex)
        {
            if (_showNotificationAction != null)
                _showNotificationAction(ex.Message, true);
        }
    }

    // --- Drag-and-drop merge/rebase between ref labels (T-09 §3.3) --------------------------
    //
    // TODO(T-09 human-review): drag-to-rebase/merge *gesture feel*. Dragging branch-label A onto
    // label B should pop the two-action flyout built by BuildDragActionMenu below. The flyout
    // content + checkout-gating logic and its routing are complete and tested; what remains for a
    // human is only the pointer-gesture feel in the canvas — the drag threshold, the ghost label
    // that follows the cursor, and the drop-target highlight on B. No git behavior is deferred.

    /// <summary>Source/target ref pair carried as a drag-flyout command parameter.</summary>
    public sealed record DragRefPair(string Source, string Target);

    /// <summary>
    /// Builds the drop flyout for dragging ref <paramref name="sourceRef"/> onto <paramref name="targetRef"/>:
    /// exactly two actions — merge and rebase. When the target is not checked out the merge action
    /// makes the required checkout explicit ("Checkout B, then merge A") because v1 never merges
    /// in-memory against a non-checked-out branch (a T-09 rejection trigger). Pure and testable.
    /// </summary>
    public ObservableCollection<MenuItemViewModel> BuildDragActionMenu(string sourceRef, string targetRef, bool targetIsCheckedOut)
    {
        var pair = new DragRefPair(sourceRef, targetRef);
        var mergeHeader = targetIsCheckedOut
            ? $"Merge {sourceRef} into {targetRef}"
            : $"Checkout {targetRef}, then merge {sourceRef}";

        return new ObservableCollection<MenuItemViewModel>
        {
            new MenuItemViewModel { Header = mergeHeader, Command = MergeRefsCommand, CommandParameter = pair },
            new MenuItemViewModel { Header = $"Rebase {sourceRef} onto {targetRef}", Command = RebaseRefsCommand, CommandParameter = pair }
        };
    }

    /// <summary>Convenience overload that resolves whether the target is currently checked out.</summary>
    public ObservableCollection<MenuItemViewModel> BuildDragActionMenu(string sourceRef, string targetRef)
        => BuildDragActionMenu(sourceRef, targetRef, IsRefCheckedOut(targetRef));

    private bool IsRefCheckedOut(string refName)
        => _gitService.GetBranches(_repoPath)
            .Any(b => (b.Name == refName || b.FriendlyName == refName) && b.IsCurrentRepositoryHead);

    [RelayCommand]
    private System.Threading.Tasks.Task MergeRefs(DragRefPair pair) => RunGitActionAsync(() =>
    {
        // Never merge in-memory against a non-checked-out branch: check the target out first.
        if (!IsRefCheckedOut(pair.Target))
        {
            _gitService.CheckoutBranch(_repoPath, pair.Target);
        }
        _gitService.Merge(_repoPath, pair.Source);
    });

    [RelayCommand]
    private System.Threading.Tasks.Task RebaseRefs(DragRefPair pair) => RunGitActionAsync(() =>
    {
        // Rebase source onto target: source must be checked out for the rebase to move it.
        if (!IsRefCheckedOut(pair.Source))
        {
            _gitService.CheckoutBranch(_repoPath, pair.Source);
        }
        _gitService.Rebase(_repoPath, pair.Target);
    });

    // --- Pinning (T-09 §3.4) ----------------------------------------------------------------

    [RelayCommand]
    private void PinRef(string refName)
    {
        _pinnedRefService.Pin(_repoPath, refName);
        LoadInitialCommits();
    }

    [RelayCommand]
    private void UnpinRef(string refName)
    {
        _pinnedRefService.Unpin(_repoPath, refName);
        LoadInitialCommits();
    }

    // --- Delete key on a selected ref label (T-09 §3.5) -------------------------------------

    [RelayCommand]
    private async System.Threading.Tasks.Task DeleteSelectedRef()
    {
        var refName = SelectedRefName;
        if (string.IsNullOrEmpty(refName)) return;

        bool confirmed = await _confirmationService.ConfirmAsync(
            "Delete branch",
            $"Are you sure you want to delete the branch '{refName}'?\nThis action cannot be undone.",
            "Delete");
        if (!confirmed) return;

        await RunGitActionAsync(() => _gitService.DeleteBranch(_repoPath, refName, false));
        _pinnedRefService.Unpin(_repoPath, refName); // drop any pin for a now-gone ref
        BranchBrowser.LoadBranches();
        SelectedRefName = null;
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task CreateBranchHere(string sha)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop && desktop.MainWindow != null)
        {
            var vm = new CreateBranchDialogViewModel();
            var dialog = new Views.CreateBranchDialog { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            if (!vm.IsConfirmed) return;

            await RunGitActionAsync(() => _gitService.CreateBranchAt(_repoPath, vm.BranchName, sha, vm.CheckoutImmediately));
            BranchBrowser.LoadBranches();
            _showNotificationAction?.Invoke($"Created branch '{vm.BranchName}'.", false);
        }
    }

    // --- Context-menu construction (testable: lives in the ViewModel, not the canvas) -------

    /// <summary>
    /// Builds the commit context menu for <paramref name="sha"/>, applying the T-09 context rules:
    /// "Checkout (detached)" is hidden on the HEAD commit, and the "Reset current branch here"
    /// submenu is hidden when HEAD is detached or unborn (there is no branch to move). Hard reset
    /// is present but always routes through the confirmation gate.
    /// </summary>
    public ObservableCollection<MenuItemViewModel> BuildCommitMenu(string sha)
    {
        var head = _gitService.GetHeadState(_repoPath);
        bool isHeadCommit = head.Sha != null && string.Equals(head.Sha, sha, StringComparison.OrdinalIgnoreCase);

        var items = new ObservableCollection<MenuItemViewModel>();

        if (!isHeadCommit)
        {
            items.Add(new MenuItemViewModel { Header = "Checkout (detached)", Command = CheckoutRevisionCommand, CommandParameter = sha });
        }
        items.Add(new MenuItemViewModel { Header = "Create branch here…", Command = CreateBranchHereCommand, CommandParameter = sha });
        items.Add(new MenuItemViewModel { Header = "Create tag here…", Command = CreateTagCommand, CommandParameter = sha });

        items.Add(new SeparatorViewModel());

        items.Add(new MenuItemViewModel { Header = "Cherry-pick", Command = CherryPickCommitCommand, CommandParameter = sha });
        items.Add(new MenuItemViewModel { Header = "Revert", Command = RevertCommitCommand, CommandParameter = sha });

        if (!head.IsDetached && !head.IsUnborn)
        {
            var reset = new MenuItemViewModel { Header = "Reset current branch here" };
            reset.SubItems.Add(new MenuItemViewModel { Header = "Soft — keep changes staged", Command = ResetCommitSoftCommand, CommandParameter = sha });
            reset.SubItems.Add(new MenuItemViewModel { Header = "Mixed — keep changes unstaged", Command = ResetCommitMixedCommand, CommandParameter = sha });
            reset.SubItems.Add(new MenuItemViewModel { Header = "Hard — discard changes…", Command = ResetCommitHardCommand, CommandParameter = sha });
            items.Add(reset);
        }

        items.Add(new MenuItemViewModel { Header = "Interactive rebase onto here…", Command = InteractiveRebaseCommand, CommandParameter = sha });

        items.Add(new SeparatorViewModel());

        // Retained from the previous menu (outside the T-09 core contract, but useful):
        // review, message edit, and graph navigation.
        items.Add(new MenuItemViewModel { Header = "Diff working tree against this commit", Command = DiffWorkingTreeAgainstCommitCommand, CommandParameter = sha });
        items.Add(new MenuItemViewModel { Header = "Edit commit message", Command = AmendCommitCommand, CommandParameter = sha, IsEnabled = CanAmendCommit(sha) });
        items.Add(new MenuItemViewModel { Header = "Go to parent commit", Command = GoToParentCommitCommand, CommandParameter = sha });
        items.Add(new MenuItemViewModel { Header = "Go to child commit", Command = GoToChildCommitCommand, CommandParameter = sha });

        items.Add(new SeparatorViewModel());

        items.Add(new MenuItemViewModel { Header = "Copy SHA", Command = CopyCommitHashCommand, CommandParameter = sha });

        return items;
    }

    /// <summary>
    /// Routes a <see cref="GraphHit"/> to the right menu: a Node hit → the commit menu; a Label
    /// hit → the existing branch/tag menu (Phase-4.3, reused from <see cref="BranchBrowser"/>); a
    /// None hit (empty space / unborn graph) → <c>null</c>, i.e. no menu.
    /// </summary>
    public ObservableCollection<MenuItemViewModel>? BuildContextMenuForHit(GraphHit hit)
    {
        switch (hit.Kind)
        {
            case GraphHitKind.Node when hit.Sha != null:
                return BuildCommitMenu(hit.Sha);
            case GraphHitKind.Label when hit.RefName != null:
                // Selecting the label arms the Delete-key shortcut (T-09 §3.5).
                SelectedRefName = hit.RefName;
                var refMenu = BranchBrowser.BuildRefMenu(hit.RefName);
                if (refMenu == null) return null;
                var items = refMenu.SubItems;
                items.Add(new SeparatorViewModel());
                items.Add(_pinnedRefService.IsPinned(_repoPath, hit.RefName)
                    ? new MenuItemViewModel { Header = "Unpin", Command = UnpinRefCommand, CommandParameter = hit.RefName }
                    : new MenuItemViewModel { Header = "Pin", Command = PinRefCommand, CommandParameter = hit.RefName });
                return items;
            default:
                return null;
        }
    }
}
