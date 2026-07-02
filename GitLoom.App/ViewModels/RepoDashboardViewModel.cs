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

    [ObservableProperty]
    private bool _isSshPassphrasePromptVisible;

    [ObservableProperty]
    private string _sshPassphraseInput = string.Empty;

    private string _pendingAction = string.Empty;

    public StagingPanelViewModel StagingPanel { get; }
    public DiffViewerViewModel DiffViewer { get; }
    public CommitTimelineViewModel CommitTimeline { get; }
    public BranchBrowserViewModel BranchBrowser { get; }

    public RepoDashboardViewModel(Repository repository)
    {
        _repoPath = repository.Path;
        RepositoryName = repository.DisplayName;
        _gitService = new GitService();

        StagingPanel = new StagingPanelViewModel(_gitService, _repoPath, () =>
        {
            _watcher?.ForceRefresh();
        }, (msg, isError) =>
        {
            ShowNotification(msg, isError);
        });
        DiffViewer = new DiffViewerViewModel(_gitService, _repoPath);
        CommitTimeline = new CommitTimelineViewModel(_gitService, _repoPath);
        BranchBrowser = new BranchBrowserViewModel(_gitService, _repoPath,
            onBranchChangedAction: () =>
            {
                Dispatcher.UIThread.Post(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(500);
                    _watcher?.ForceRefresh();
                });
            },
            showNotificationAction: (msg) =>
            {
                ShowNotification(msg, msg.Contains("fail", System.StringComparison.OrdinalIgnoreCase) || msg.Contains("Error", System.StringComparison.OrdinalIgnoreCase));
            },
            onCompareBranchAction: (branchName) =>
            {
                CommitTimeline.LoadInitialCommits(branchName);
            }
        );

        StagingPanel.OnFileHistoryRequested += (filePath) =>
        {
            CommitTimeline.LoadInitialCommits(filterFilePath: filePath);
        };

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

    // True while a network git op is running; disables the toolbar commands and
    // drives a progress overlay so the UI never appears frozen.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    [NotifyCanExecuteChangedFor(nameof(PullCommand))]
    [NotifyCanExecuteChangedFor(nameof(FetchCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateProjectCommand))]
    private bool _isBusy;

    private bool CanRunGitAction() => !IsBusy;

    private async System.Threading.Tasks.Task RefreshStatusAsync()
    {
        IsLoading = true;

        await System.Threading.Tasks.Task.Run(() =>
        {
            var allChanges = _gitService.GetRepositoryStatus(_repoPath);
            Dispatcher.UIThread.Post(() =>
            {
                StagingPanel.UpdateStatus(allChanges);
                StagingPanel.LoadStashes();
            });

            var aheadBehind = _gitService.GetAheadBehind(_repoPath);
            Dispatcher.UIThread.Post(() =>
            {
                AheadCount = aheadBehind.Ahead;
                BehindCount = aheadBehind.Behind;
            });
        });

        CommitTimeline.LoadInitialCommits();
        BranchBrowser.LoadBranches();

        IsLoading = false;
    }

    [RelayCommand]
    private void SaveSshPassphrase()
    {
        var keyring = new GitLoom.Core.Security.SecureKeyring();
        keyring.SaveSecret("ssh_passphrase", SshPassphraseInput);
        IsSshPassphrasePromptVisible = false;
        SshPassphraseInput = string.Empty;

        // Retry the pending action
        if (_pendingAction == "Push") PushCommand.Execute(null);
        else if (_pendingAction == "Pull") PullCommand.Execute(null);
        else if (_pendingAction == "Fetch") FetchCommand.Execute(null);
        else if (_pendingAction == "UpdateProject") UpdateProjectCommand.Execute(null);
    }

    [RelayCommand]
    private void CancelSshPassphrase()
    {
        IsSshPassphrasePromptVisible = false;
        SshPassphraseInput = string.Empty;
        ShowNotification("Action cancelled because SSH passphrase was not provided.", true);
    }

    // Returns the exception of type T whether it is the thrown exception itself
    // or wrapped as its InnerException, so callers can surface the typed
    // exception's own actionable message rather than an outer wrapper's text.
    private static T? Unwrap<T>(System.Exception ex) where T : class
        => ex as T ?? ex.InnerException as T;

    private void HandleGitActionException(System.Exception ex, string actionName)
    {
        if (Unwrap<GitLoom.Core.Exceptions.SshAuthenticationException>(ex) is not null)
        {
            _pendingAction = actionName;
            IsSshPassphrasePromptVisible = true;
        }
        else if (Unwrap<GitLoom.Core.Exceptions.MergeConflictException>(ex) is { } conflict)
        {
            // Not a hard failure: the repo is now in a conflicted state. Surface
            // guidance (not an error toast) and refresh so the conflicted files
            // appear in the staging panel for resolution.
            ShowNotification(conflict.Message, false);
            _ = RefreshStatusAsync();
        }
        else if (Unwrap<GitLoom.Core.Exceptions.GitIdentityMissingException>(ex) is { } identity)
        {
            ShowNotification(identity.Message, true);
        }
        else
        {
            ShowNotification($"{actionName} Failed: {ex.Message}", true);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.Push(_repoPath), ct);
            ShowNotification("Push completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Push cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "Push"); }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PullAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.Pull(_repoPath), ct);
            ShowNotification("Pull completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Pull cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "Pull"); }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task FetchAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.Fetch(_repoPath), ct);
            BranchBrowser.LoadBranches();
            ShowNotification("Fetch completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Fetch cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "Fetch"); }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task UpdateProjectAsync(System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            await System.Threading.Tasks.Task.Run(() => _gitService.UpdateProject(_repoPath), ct);
            ShowNotification("Project updated successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification("Update cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, "UpdateProject"); }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }
}
