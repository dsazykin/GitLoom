using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

public partial class RepoDashboardViewModel : ViewModelBase, System.IDisposable
{
    private readonly string _repoPath;
    private readonly IGitService _gitService;
    // Operation journal (T-19): shared SQLite-backed store. Instances are stateless (all
    // state lives in the DB), so the GitService, InteractiveRebaseService, and the history
    // panel each hold their own instance pointing at the same default AppDbContext.
    private readonly IOperationJournal _journal = new OperationJournal();
    private readonly RepositoryWatcher _watcher;

    // Shared HttpClient for the T-23 pull-request providers — one process-wide instance so opening the
    // PR panel repeatedly never leaks sockets (per-call `new HttpClient` is a rejection trigger).
    private static readonly System.Net.Http.HttpClient _prHttpClient = new();

    // Background auto-fetch (T-10): keeps ahead/behind fresh off the UI thread. The
    // cadence comes from UserPreferences.AutoFetchMinutes (0 disables it).
    private readonly AutoFetchService _autoFetch;
    private System.Threading.Timer? _lastFetchedTicker;

    [ObservableProperty]
    private string _repositoryName;

    [ObservableProperty]
    private int? _aheadCount;

    [ObservableProperty]
    private int? _behindCount;

    // "Fetched N min ago" surfaced next to the ahead/behind badge; dims when stale (T-10 / 1.12).
    [ObservableProperty]
    private string _lastFetchedText = string.Empty;

    [ObservableProperty]
    private bool _isLastFetchStale;

    private System.DateTimeOffset? _lastFetched;

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

    // Opens another path as its own top-level repository (used by the submodules panel's
    // "open as its own repo" action). Wired from MainWindowViewModel; null in isolated tests.
    private readonly System.Action<string>? _openRepositoryPath;

    public RepoDashboardViewModel(Repository repository, System.Action<string>? openRepositoryPath = null)
    {
        _repoPath = repository.Path;
        RepositoryName = repository.DisplayName;
        _openRepositoryPath = openRepositoryPath;
        // Feed the live signing preferences to the git service so an enabled "Sign Commits"
        // toggle takes effect on the next commit/tag without a restart (T-15).
        _gitService = new GitService(
            () => GitLoom.App.App.Settings?.Current ?? new GitLoom.Core.Models.UserPreferences(),
            _journal);

        StagingPanel = new StagingPanelViewModel(_gitService, _repoPath, () =>
        {
            _watcher?.ForceRefresh();
        }, (msg, isError) =>
        {
            ShowNotification(msg, isError);
        });
        DiffViewer = new DiffViewerViewModel(_gitService, _repoPath,
            onStagingChanged: () => _watcher?.ForceRefresh(),
            settings: GitLoom.App.App.Settings);
        DiffViewer.FileHistoryRequested += (filePath) => _ = OpenFileHistoryAsync(filePath);
        CommitTimeline = new CommitTimelineViewModel(_gitService, _repoPath, ShowNotification);
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
            },
            onCreatePullRequestAction: () => _ = OpenPullRequestsAsync(beginCreate: true)
        );

        StagingPanel.OnFileHistoryRequested += (filePath) =>
        {
            _ = OpenFileHistoryAsync(filePath);
        };

        StagingPanel.SelectedFileChanged += (file) => DiffViewer.UpdateDiff(file);

        // Load immediately
        _ = RefreshStatusAsync();

        // Start listening for background folder changes
        _watcher = new RepositoryWatcher(_repoPath);
        _watcher.RepositoryChanged += OnRepositoryChanged;

        // Background auto-fetch: on each successful fetch, refresh ahead/behind and the
        // "last fetched" label on the UI thread. Failures stay silent (no toast spam).
        _autoFetch = new AutoFetchService(_gitService,
            () => GitLoom.App.App.Settings?.Current ?? new GitLoom.Core.Models.UserPreferences());
        _autoFetch.Fetched += OnAutoFetched;
        _autoFetch.Watch(_repoPath);

        // Refresh the relative "N min ago" label once a minute so it ages correctly.
        _lastFetchedTicker = new System.Threading.Timer(
            _ => Dispatcher.UIThread.Post(UpdateLastFetchedLabel), null,
            System.TimeSpan.FromMinutes(1), System.TimeSpan.FromMinutes(1));
    }

    private void OnAutoFetched(string repoPath)
    {
        _lastFetched = _autoFetch.GetLastFetched(repoPath);
        Dispatcher.UIThread.Post(async () =>
        {
            UpdateLastFetchedLabel();
            await RefreshStatusAsync();
        });
    }

    private void UpdateLastFetchedLabel()
    {
        if (_lastFetched is not { } when)
        {
            LastFetchedText = string.Empty;
            IsLastFetchStale = false;
            return;
        }

        var elapsed = System.DateTimeOffset.Now - when;
        var minutes = (int)elapsed.TotalMinutes;
        LastFetchedText = minutes <= 0
            ? "Fetched just now"
            : minutes == 1 ? "Fetched 1 min ago" : $"Fetched {minutes} min ago";
        // Dimmed once the picture is more than 15 minutes old (closes the 1.12 stale badge).
        IsLastFetchStale = minutes > 15;
    }

    private void OnRepositoryChanged()
    {
        Dispatcher.UIThread.InvokeAsync(async () => await RefreshStatusAsync());
    }

    // --- Command palette surface (T-18) ---

    /// <summary>The open repository's path, exposed so palette actions (e.g. Analytics) can target it.</summary>
    public string RepositoryPath => _repoPath;

    /// <summary>Local branches for the palette to offer as checkout targets. Returns empty on any read error.</summary>
    public System.Collections.Generic.IReadOnlyList<GitBranchItem> ListLocalBranches()
    {
        try
        {
            return _gitService.GetBranches(_repoPath).Where(b => !b.IsRemote).ToList();
        }
        catch
        {
            return System.Array.Empty<GitBranchItem>();
        }
    }

    /// <summary>Checks out a branch selected from the palette (delegates to the branch browser's command).</summary>
    public void CheckoutBranchFromPalette(GitBranchItem branch) => BranchBrowser.CheckoutBranchCommand.Execute(branch);

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
            // guidance (not an error toast) and open the resolver so the user can
            // resolve base/ours/theirs and complete the merge/rebase.
            ShowNotification(conflict.Message, false);
            _ = ShowConflictResolverAsync();
        }
        else if (Unwrap<GitLoom.Core.Exceptions.GitIdentityMissingException>(ex) is { } identity)
        {
            ShowNotification(identity.Message, true);
        }
        else if (Unwrap<GitLoom.Core.Exceptions.AuthenticationRequiredException>(ex) is { } auth)
        {
            // T-14: when the failing host is known, route straight to the Accounts page
            // (PAT dialog) for that host; otherwise show actionable guidance.
            if (!string.IsNullOrEmpty(auth.Host))
            {
                ShowNotification($"{actionName} failed: sign in to {auth.Host} to continue.", true);
                _ = OpenAccountsAsync(auth.Host);
            }
            else
            {
                ShowNotification(
                    $"{actionName} failed: authentication required. Open Accounts to sign in or store a token for this host.",
                    true);
            }
        }
        else
        {
            ShowNotification($"{actionName} Failed: {ex.Message}", true);
        }
    }

    // Opens the conflict resolver over the main window, then refreshes so resolved
    // state (or an abort) is reflected. Routed to from the MergeConflictException branch.
    private async System.Threading.Tasks.Task ShowConflictResolverAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.ConflictedFilesWindow();
            dialog.DataContext = new ConflictedFilesViewModel(_repoPath, _gitService, new MergeDiffService(), dialog);
            await dialog.ShowDialog(desktop.MainWindow);
        }
        await RefreshStatusAsync();
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

    // ---- Push options (T-10) ------------------------------------------------
    // Each resolves the target remote (tracked → origin → sole) in Core; the current
    // branch comes from the branch browser. force-with-lease is the only force path.

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushForceWithLeaseAsync(System.Threading.CancellationToken ct)
        => await RunPushOptionAsync("Force push (with lease)", (remote, branch) =>
            _gitService.PushForceWithLease(_repoPath, remote, branch), ct);

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushSetUpstreamAsync(System.Threading.CancellationToken ct)
        => await RunPushOptionAsync("Push (set upstream)", (remote, branch) =>
            _gitService.PushSetUpstream(_repoPath, remote, branch), ct);

    [RelayCommand(CanExecute = nameof(CanRunGitAction))]
    private async System.Threading.Tasks.Task PushTagsAsync(System.Threading.CancellationToken ct)
        => await RunPushOptionAsync("Push tags", (remote, _) =>
            _gitService.PushTags(_repoPath, remote), ct);

    private async System.Threading.Tasks.Task RunPushOptionAsync(
        string label, System.Action<string, string> op, System.Threading.CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var branch = BranchBrowser.CurrentBranchName;
            await System.Threading.Tasks.Task.Run(() =>
            {
                var remote = _gitService.GetDefaultRemoteName(_repoPath);
                op(remote, branch);
            }, ct);
            ShowNotification($"{label} completed successfully.", false);
        }
        catch (System.OperationCanceledException) { ShowNotification($"{label} cancelled.", false); }
        catch (System.Exception ex) { HandleGitActionException(ex, label); }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ManageRemotesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.RemotesWindow
            {
                DataContext = new RemotesViewModel(_gitService, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh())
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    // Operation history panel (T-19): per-entry undo/redo of journaled operations.
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageOperationHistoryAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.OperationHistoryWindow
            {
                DataContext = new OperationHistoryViewModel(_journal, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh())
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    // Reflog viewer & recovery (T-20): per-ref reflog with restore (journaled hard reset) and
    // create-branch-here (journaled orphan-tip recovery).
    [RelayCommand]
    private async System.Threading.Tasks.Task ViewReflogAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.ReflogWindow
            {
                DataContext = new ReflogViewModel(_gitService, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh())
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    // Pull Requests panel (T-23): host-agnostic PR list/create/merge/close over IPullRequestService
    // (GitHub v1). Gracefully shows an unsupported/sign-in state when the origin host has no provider
    // or no stored token. beginCreate=true (from the branch context menu / palette) opens the create form.
    [RelayCommand]
    private System.Threading.Tasks.Task ManagePullRequests() => OpenPullRequestsAsync(beginCreate: false);

    /// <summary>Opens the Pull Requests panel; <paramref name="beginCreate"/> jumps straight to the create form.</summary>
    public async System.Threading.Tasks.Task OpenPullRequestsAsync(bool beginCreate)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var service = new GitLoom.Core.Services.PullRequestService(_gitService, httpClient: _prHttpClient);
            var vm = new PullRequestsViewModel(service, _gitService, _repoPath);
            if (beginCreate && vm.BeginCreateCommand.CanExecute(null))
                vm.BeginCreateCommand.Execute(null);
            var dialog = new Views.PullRequestsWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    // Worktree management panel (T-21) over the T-07 porcelain backend: list, add (existing/new branch),
    // open, remove/force, prune. Branch-already-checked-out is validated in the VM (create disabled).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageWorktreesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.WorktreeWindow
            {
                DataContext = new WorktreePanelViewModel(_gitService, _repoPath,
                    onOpenWorktree: _openRepositoryPath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    // Git identity profiles (T-21): CRUD + apply-to-this-repo (writes local user.name/email/signing config).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageProfilesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.ProfilesWindow
            {
                DataContext = new ProfilesViewModel(new ProfileService(() => new GitLoom.Core.AppDbContext(), _gitService), _repoPath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    // Submodules panel (T-16): list + init/update, update-to-remote, sync, open-as-repo.
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageSubmodulesAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.SubmodulesWindow
            {
                DataContext = new SubmodulesViewModel(_gitService, _repoPath,
                    onChanged: () => _watcher?.ForceRefresh(),
                    openRepository: _openRepositoryPath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    // Git LFS panel (T-17): per-repo enable toggle, tracked patterns, LFS objects, pull, prune.
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageLfsAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            // LfsService composes the concrete GitService so LFS network ops reuse the one
            // audited authenticated CLI path (T-14). _gitService is always a GitService here.
            var lfs = new LfsService((GitService)_gitService);
            var dialog = new Views.LfsWindow
            {
                DataContext = new LfsViewModel(lfs, _repoPath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private System.Threading.Tasks.Task ManageAccountsAsync() => OpenAccountsAsync(null);

    // Opens the Accounts preferences page (T-14). When routed from an auth failure,
    // focusHost names the host that needs a token so the PAT dialog is pre-added.
    private async System.Threading.Tasks.Task OpenAccountsAsync(string? focusHost)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            // Device-flow sign-in presents its code through the existing DeviceFlowAuthDialog.
            var authContext = new GitLoom.Core.Sync.HostAuthContext
            {
                PresentDeviceCode = device =>
                {
                    var dlg = new Views.DeviceFlowAuthDialog(device.VerificationUri, device.UserCode);
                    _ = dlg.ShowDialog(desktop.MainWindow);
                    return System.Threading.Tasks.Task.CompletedTask;
                }
            };
            var vm = new AccountsViewModel(authContext: authContext);
            if (!string.IsNullOrEmpty(focusHost))
            {
                vm.NewHost = focusHost;
                vm.AddCustomHostCommand.Execute(null);
            }
            var dialog = new Views.AccountsWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    [RelayCommand]
    private async System.Threading.Tasks.Task ManageSshKeysAsync()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.SshKeysWindow { DataContext = new SshKeysViewModel() };
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    /// <summary>Opens the dedicated file-history dialog (T-12) for a repo-relative path. Shared by
    /// the staging-panel and diff-viewer "Show History" entry points.</summary>
    public async System.Threading.Tasks.Task OpenFileHistoryAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var dialog = new Views.FileHistoryView
            {
                DataContext = new FileHistoryViewModel(_gitService, _repoPath, filePath)
            };
            await dialog.ShowDialog(desktop.MainWindow);
        }
    }

    public void Dispose()
    {
        _autoFetch.Fetched -= OnAutoFetched;
        _autoFetch.Dispose();
        _lastFetchedTicker?.Dispose();
        _notificationTimer?.Dispose();
        _watcher.RepositoryChanged -= OnRepositoryChanged;
        _watcher.Dispose();
    }
}
