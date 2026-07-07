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

    // Shared HttpClient for the host-integration providers (T-23 pull requests, T-24 issues) — one
    // process-wide instance so opening those panels repeatedly never leaks sockets (a per-call
    // `new HttpClient` is a rejection trigger).
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
        },
        scanner: new GitLoom.Core.Services.PreCommitScanner(_gitService),
        preferences: () => GitLoom.App.App.Settings?.Current ?? new GitLoom.Core.Models.UserPreferences(),
        settings: GitLoom.App.App.Settings);
        DiffViewer = new DiffViewerViewModel(_gitService, _repoPath,
            onStagingChanged: () => _watcher?.ForceRefresh(),
            settings: GitLoom.App.App.Settings);
        DiffViewer.FileHistoryRequested += (filePath) => _ = OpenFileHistoryAsync(filePath);
        DiffViewer.BlameRequested += (filePath) => _ = OpenBlameAsync(filePath);
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
            onCreatePullRequestAction: () => _ = OpenPullRequestsAsync(beginCreate: true),
            onCheckoutInWorktreeAction: branch => _ = CheckoutBranchInWorktreeAsync(branch)
        );

        StagingPanel.OnFileHistoryRequested += (filePath) =>
        {
            _ = OpenFileHistoryAsync(filePath);
        };

        StagingPanel.OnBlameRequested += (filePath) =>
        {
            _ = OpenBlameAsync(filePath);
        };

        StagingPanel.SelectedFileChanged += (file) => DiffViewer.UpdateDiff(file);

        // Load immediately
        _ = LoadRepositoryAsync();

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
            await RefreshCoreAsync();
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
        Dispatcher.UIThread.InvokeAsync(async () => await RefreshCoreAsync());
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

    // First open only (constructor): shows the full-panel "Parsing Repository..." loading screen
    // while the initial status/timeline/branch data loads.
    private async System.Threading.Tasks.Task LoadRepositoryAsync()
    {
        IsLoading = true;
        await RefreshCoreAsync();
        IsLoading = false;
    }

    // Every subsequent refresh (file-watcher change, post-commit/checkout, post-push/pull/fetch,
    // after a Manage* dialog closes, auto-fetch) — refreshes status/timeline/branches in place
    // WITHOUT the full-screen loading overlay, so a single commit doesn't blank the dashboard (#64).
    private async System.Threading.Tasks.Task RefreshCoreAsync()
    {
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
        await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            var vm = new PullRequestsViewModel(service, _gitService, _repoPath,
                openUrl: null,
                pickWorktreeFolder: PickWorktreeTargetAsync,
                openWorktree: path => _openRepositoryPath?.Invoke(path));
            if (beginCreate && vm.BeginCreateCommand.CanExecute(null))
                vm.BeginCreateCommand.Execute(null);
            var dialog = new Views.PullRequestsWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Folder pick for a PR/branch worktree (T-29). Given a default full worktree path (`../<repo>-pr-<n>`),
    // prompts for the PARENT directory (a folder picker selects existing dirs; `git worktree add` creates the
    // leaf), then returns `<chosen parent>/<leaf>`. Returns null when the user cancels.
    private async System.Threading.Tasks.Task<string?> PickWorktreeTargetAsync(string defaultPath)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var trimmed = defaultPath.TrimEnd('/', '\\');
            var leaf = System.IO.Path.GetFileName(trimmed);
            var parent = System.IO.Path.GetDirectoryName(trimmed);
            var storageProvider = desktop.MainWindow.StorageProvider;
            var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "Choose where to create the worktree",
                AllowMultiple = false,
            };
            if (!string.IsNullOrEmpty(parent))
            {
                try { options.SuggestedStartLocation = await storageProvider.TryGetFolderFromPathAsync(new System.Uri(parent)); }
                catch { /* best-effort start location */ }
            }
            var result = await storageProvider.OpenFolderPickerAsync(options);
            if (result.Count > 0)
                return System.IO.Path.Combine(result[0].Path.LocalPath, leaf);
            return null;
        }
        return defaultPath;
    }

    // Branch → new worktree (T-29). Picks a target folder (default `../<repo>-<branch-leaf>`), creates a
    // worktree checked out to the (local or remote-tracking) branch off the UI thread, then opens it as a
    // repo. Remote-tracking refs get a local tracking branch created first (in the service).
    public async System.Threading.Tasks.Task CheckoutBranchInWorktreeAsync(GitLoom.Core.Models.GitBranchItem branch)
    {
        var trimmed = _repoPath.TrimEnd('/', '\\');
        var parent = System.IO.Path.GetDirectoryName(trimmed) ?? trimmed;
        var repoName = System.IO.Path.GetFileName(trimmed);
        var branchLeaf = branch.FriendlyName.Split('/').Last();
        var defaultPath = System.IO.Path.Combine(parent, $"{repoName}-{branchLeaf}");

        var target = await PickWorktreeTargetAsync(defaultPath);
        if (string.IsNullOrWhiteSpace(target)) return;

        try
        {
            var created = await System.Threading.Tasks.Task.Run(
                () => _gitService.CheckoutBranchWorktree(_repoPath, branch.FriendlyName, target));
            ShowNotification($"Checked out '{branch.FriendlyName}' into a new worktree.");
            _openRepositoryPath?.Invoke(created);
        }
        catch (System.Exception ex)
        {
            ShowNotification(ex.Message, isError: true);
        }
    }

    // Issues panel (T-24): host-agnostic issue list/create/comment/close over IIssueService (GitHub v1).
    // Gracefully shows an unsupported/sign-in state when the origin host has no provider or no stored
    // token. Reuses the same shared HttpClient as the PR panel (no second client).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageIssues()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var service = new GitLoom.Core.Services.IssueService(_gitService, httpClient: _prHttpClient);
            var vm = new IssuesViewModel(service, _repoPath);
            var dialog = new Views.IssuesWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Notifications inbox (T-27): the authenticated user's notifications for the origin host over
    // INotificationService (GitHub v1), grouped by repo with mark-read / mark-all / open + an unread-only
    // toggle. Gracefully shows an unsupported/sign-in state when the origin host has no provider or no
    // stored token. Reuses the same shared HttpClient as the PR/issue panels (no second client).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageNotifications()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var service = new GitLoom.Core.Services.NotificationService(_gitService, httpClient: _prHttpClient);
            var vm = new NotificationsViewModel(service, _repoPath);
            var dialog = new Views.NotificationsWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
        }
    }

    // Releases panel (T-28): host-agnostic release list/create over IReleaseService (GitHub v1) plus a
    // fully-local "auto-generate notes" (walks the commit history through the pure ChangelogGenerator — no
    // network). Gracefully shows an unsupported/sign-in state when the origin host has no provider or no
    // stored token. Reuses the same shared HttpClient as the PR/issue panels (no second client).
    [RelayCommand]
    private async System.Threading.Tasks.Task ManageReleases()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var service = new GitLoom.Core.Services.ReleaseService(_gitService, httpClient: _prHttpClient);
            var vm = new ReleasesViewModel(service, _gitService, _repoPath);
            var dialog = new Views.ReleasesWindow { DataContext = vm };
            await dialog.ShowDialog(desktop.MainWindow);
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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
            await RefreshCoreAsync();
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

    /// <summary>Opens the dedicated blame dialog (T-33) for a repo-relative path — the entry point that
    /// makes the T-11 blame gutter and the T-32 "Why this line" PR/issue popover reachable. Shared by the
    /// staging-panel and diff-viewer "Blame this file" entry points. The commit-context service (T-32) is
    /// constructed on the shared <see cref="_prHttpClient"/> and its jumps route into the in-app PR /
    /// Issues panels (falling back to the browser only when a panel can't be shown).</summary>
    public async System.Threading.Tasks.Task OpenBlameAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        if (Avalonia.Application.Current?.ApplicationLifetime is
                Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow != null)
        {
            var commitContext = new GitLoom.Core.Services.CommitContextService(_gitService, httpClient: _prHttpClient);
            var vm = new BlameViewModel(_gitService, _repoPath, commitContext,
                openPullRequest: pr => { _ = OpenPullRequestsAsync(beginCreate: false); },
                openLinkedIssue: issue => ManageIssuesCommand.Execute(null))
            {
                FilePath = (filePath ?? string.Empty).Replace('\\', '/')
            };
            var dialog = new Views.BlameWindow { DataContext = vm };
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
