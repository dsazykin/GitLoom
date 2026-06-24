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

    [ObservableProperty]
    private string _notificationMessage = string.Empty;

    [ObservableProperty]
    private bool _isNotificationVisible = false;

    [ObservableProperty]
    private bool _isErrorNotification = false;

    private System.Threading.Timer? _notificationTimer;

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
        }, (msg, isError) => {
            ShowNotification(msg, isError);
        });
        DiffViewer = new DiffViewerViewModel(_gitService, _repoPath);
        CommitTimeline = new CommitTimelineViewModel(_gitService, _repoPath);
        BranchBrowser = new BranchBrowserViewModel(_gitService, _repoPath, 
            onBranchChangedAction: () => {
                Dispatcher.UIThread.Post(async () => { 
                    await System.Threading.Tasks.Task.Delay(500); 
                    _watcher?.ForceRefresh(); 
                });
            },
            showNotificationAction: (msg) => {
                ShowNotification(msg, msg.Contains("fail", System.StringComparison.OrdinalIgnoreCase) || msg.Contains("Error", System.StringComparison.OrdinalIgnoreCase));
            },
            onCompareBranchAction: (branchName) => {
                CommitTimeline.LoadInitialCommits(branchName);
            }
        );

        StagingPanel.SelectedFileChanged += (file) => DiffViewer.UpdateDiff(file);

        // Load immediately
        _ = RefreshStatusAsync();

        // Start listening for background folder changes
        _watcher = new RepositoryWatcher(_repoPath);
        _watcher.RepositoryChanged += OnRepositoryChanged;
    }

    private void OnRepositoryChanged()
    {
        Dispatcher.UIThread.InvokeAsync(async () => await RefreshStatusAsync());
    }

    public void ShowNotification(string message, bool isError = false)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            NotificationMessage = message;
            IsErrorNotification = isError;
            IsNotificationVisible = true;
            
            _notificationTimer?.Dispose();
            _notificationTimer = new System.Threading.Timer(_ =>
            {
                Dispatcher.UIThread.InvokeAsync(() => IsNotificationVisible = false);
            }, null, 3000, System.Threading.Timeout.Infinite);
        });
    }

    [ObservableProperty]
    private bool _isLoading = true;

    private async System.Threading.Tasks.Task RefreshStatusAsync()
    {
        IsLoading = true;
        
        await System.Threading.Tasks.Task.Run(() =>
        {
            var allChanges = _gitService.GetRepositoryStatus(_repoPath);
            Dispatcher.UIThread.Post(() => {
                StagingPanel.UpdateStatus(allChanges);
                StagingPanel.LoadStashes();
            });

            var aheadBehind = _gitService.GetAheadBehind(_repoPath);
            Dispatcher.UIThread.Post(() => {
                AheadCount = aheadBehind.Ahead;
                BehindCount = aheadBehind.Behind;
            });
        });

        CommitTimeline.LoadInitialCommits();
        BranchBrowser.LoadBranches();
        
        IsLoading = false;
    }

    [RelayCommand]
    private void Push()
    {
        try
        {
            _gitService.Push(_repoPath);
            ShowNotification("Push completed successfully.", false);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Push Failed: {ex.Message}");
            ShowNotification($"Push Failed: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void Pull()
    {
        try
        {
            _gitService.Pull(_repoPath);
            ShowNotification("Pull completed successfully.", false);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Pull Failed: {ex.Message}");
            ShowNotification($"Pull Failed: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void Fetch()
    {
        try
        {
            _gitService.Fetch(_repoPath);
            BranchBrowser.LoadBranches();
            _ = RefreshStatusAsync();
            ShowNotification("Fetch completed successfully.", false);
        }
        catch (System.Exception ex)
        {
            System.Console.WriteLine($"Fetch Failed: {ex.Message}");
            ShowNotification($"Fetch Failed: {ex.Message}", true);
        }
    }

    [RelayCommand]
    private void UpdateProject()
    {
        try
        {
            _gitService.UpdateProject(_repoPath);
            ShowNotification("Project updated successfully.", false);
            _ = RefreshStatusAsync();
        }
        catch (System.Exception ex)
        {
            ShowNotification($"Update Project failed: {ex.Message}", true);
        }
    }
}