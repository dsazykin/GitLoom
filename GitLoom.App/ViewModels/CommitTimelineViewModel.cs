using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Graph;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class CommitTimelineViewModel : ViewModelBase
{
    private readonly IGitService _gitService;
    private readonly string _repoPath;

    [ObservableProperty]
    private ObservableCollection<CommitRowViewModel> _commits = new();

    [ObservableProperty]
    private string? _filterBranchName;

    [ObservableProperty]
    private string? _filterFilePath;

    private readonly CommitGraphRouter _graphRouter = new();
    private GraphFringeState _currentFringe = new();

    private int _currentCommitSkip = 0;
    private const int CommitsChunkSize = 50;

    public CommitTimelineViewModel(IGitService gitService, string repoPath)
    {
        _gitService = gitService;
        _repoPath = repoPath;
    }

    public void LoadInitialCommits(string? filterBranchName = null, string? filterFilePath = null)
    {
        FilterBranchName = filterBranchName;
        FilterFilePath = filterFilePath;
        Commits.Clear();
        _currentCommitSkip = 0;
        _currentFringe = new GraphFringeState();
        LoadMoreCommits();
    }

    [RelayCommand]
    private void LoadMoreCommits()
    {
        var nextChunk = _gitService.GetRecentCommits(_repoPath, _currentCommitSkip, CommitsChunkSize, FilterBranchName, FilterFilePath).ToList();
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
