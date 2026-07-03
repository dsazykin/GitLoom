using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private void CherryPickCommit(string sha)
    {
        try
        {
            _gitService.CherryPick(_repoPath, sha);
            LoadInitialCommits();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Cherry pick failed: {ex.Message}");
        }
    }

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
        SelectedCommitFileCount = 0;

        var (files, branches) = await System.Threading.Tasks.Task.Run(() =>
        {
            var f = _gitService.GetCommitModifiedFiles(_repoPath, sha).ToList();
            var b = _gitService.GetBranchesContainingCommit(_repoPath, sha).ToList();
            return (f, b);
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
    }

    private readonly CommitGraphRouter _graphRouter = new();
    private GraphFringeState _currentFringe = new();

    private int _currentCommitSkip = 0;
    private const int CommitsChunkSize = 50;

    private readonly Action<string, bool>? _showNotificationAction;

    public CommitTimelineViewModel(IGitService gitService, string repoPath, Action<string, bool>? showNotificationAction = null)
    {
        _gitService = gitService;
        _repoPath = repoPath;
        _showNotificationAction = showNotificationAction;
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
        LoadMoreCommits();
    }

    [RelayCommand]
    public void LoadMoreCommits()
    {
        var nextChunk = _gitService.GetRecentCommits(_repoPath, _currentCommitSkip, CommitsChunkSize, SearchFilter).ToList();
        if (nextChunk.Count == 0) return;

        var routeResult = _graphRouter.RouteCommits(nextChunk, _currentFringe);

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
    private void CheckoutRevision(string sha)
    {
        try
        {
            _gitService.CheckoutRevision(_repoPath, sha);
            LoadInitialCommits();
        }
        catch { }
    }

    [RelayCommand] private void ResetCommitSoft(string sha) => ResetCommitInternal(sha, LibGit2Sharp.ResetMode.Soft);
    [RelayCommand] private void ResetCommitMixed(string sha) => ResetCommitInternal(sha, LibGit2Sharp.ResetMode.Mixed);
    [RelayCommand] private void ResetCommitHard(string sha) => ResetCommitInternal(sha, LibGit2Sharp.ResetMode.Hard);

    private void ResetCommitInternal(string sha, LibGit2Sharp.ResetMode mode)
    {
        try
        {
            _gitService.ResetToCommit(_repoPath, sha, mode);
            LoadInitialCommits();
        }
        catch { }
    }

    [RelayCommand]
    private void RevertCommit(string sha)
    {
        try
        {
            _gitService.RevertCommit(_repoPath, sha);
            LoadInitialCommits();
        }
        catch { }
    }

    private bool CanAmendCommit(string sha)
    {
        var branches = _gitService.GetBranches(_repoPath);
        var currentBranch = branches.FirstOrDefault(b => b.IsCurrentRepositoryHead);
        return currentBranch != null && currentBranch.TipSha == sha;
    }

    [RelayCommand(CanExecute = nameof(CanAmendCommit))]
    private void AmendCommit(string sha)
    {
        // TODO: show input dialog for message
        // For now, append (amended)
        try
        {
            var commit = Commits.FirstOrDefault(c => c.Commit.Sha == sha);
            if (commit != null)
            {
                _gitService.AmendCommitMessage(_repoPath, sha, commit.Commit.Message + " (amended)");
                LoadInitialCommits();
            }
        }
        catch { }
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
            var vm = new InteractiveRebaseViewModel(rebaseService, _repoPath, baseSha, _showNotificationAction);

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
}
