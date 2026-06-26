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

    public CommitTimelineViewModel(IGitService gitService, string repoPath)
    {
        _gitService = gitService;
        _repoPath = repoPath;

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
    }
}
