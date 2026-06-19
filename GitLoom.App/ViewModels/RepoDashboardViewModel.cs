using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class RepoDashboardViewModel : ViewModelBase
{
    private readonly string _repoPath;
    private readonly IGitService _gitService;
    private readonly RepositoryWatcher _watcher;

    [ObservableProperty]
    private string _repositoryName;

    [ObservableProperty]
    private int? _aheadCount;

    [ObservableProperty]
    private int? _behindCount;

    public StagingPanelViewModel StagingPanel { get; }
    public DiffViewerViewModel DiffViewer { get; }
    public CommitTimelineViewModel CommitTimeline { get; }
    public BranchBrowserViewModel BranchBrowser { get; }

    public RepoDashboardViewModel(Repository repository)
    {
        _repoPath = repository.Path;
        RepositoryName = repository.DisplayName;
        _gitService = new GitService();

        StagingPanel = new StagingPanelViewModel(_gitService, _repoPath, () => {
            _watcher?.ForceRefresh();
        });
        DiffViewer = new DiffViewerViewModel(_gitService, _repoPath);
        CommitTimeline = new CommitTimelineViewModel(_gitService, _repoPath);
        BranchBrowser = new BranchBrowserViewModel(_gitService, _repoPath, () => {
            _watcher?.ForceRefresh();
        });

        StagingPanel.SelectedFileChanged += (file) => DiffViewer.UpdateDiff(file);

        // Load immediately
        RefreshStatus();

        // Start listening for background folder changes
        _watcher = new RepositoryWatcher(_repoPath);
        _watcher.RepositoryChanged += OnRepositoryChanged;
    }

    private void OnRepositoryChanged()
    {
        Dispatcher.UIThread.InvokeAsync(RefreshStatus);
    }

    private void RefreshStatus()
    {
        var allChanges = _gitService.GetRepositoryStatus(_repoPath);
        StagingPanel.UpdateStatus(allChanges);
        StagingPanel.LoadStashes();

        var aheadBehind = _gitService.GetAheadBehind(_repoPath);
        AheadCount = aheadBehind.Ahead;
        BehindCount = aheadBehind.Behind;

        CommitTimeline.LoadInitialCommits();
        BranchBrowser.LoadBranches();
    }

    [RelayCommand]
    private void Push()
    {
        try
        {
            _gitService.Push(_repoPath);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Push Failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Pull()
    {
        try
        {
            _gitService.Pull(_repoPath);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Pull Failed: {ex.Message}");
        }
    }
}