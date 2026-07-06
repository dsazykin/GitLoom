using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Security;
using GitLoom.Core.Services;
using GitLoom.Core.Sync;
using LibGit2Sharp;

namespace GitLoom.App.ViewModels;

public partial class CloneDashboardViewModel : ViewModelBase
{
    private readonly GitHubAuthClient _authClient;
    private readonly SecureKeyring _keyring;
    private readonly ICloneService _cloneService;

    // Clone-progress state (T-21). Backed by ICloneService; the live progress-bar *animation feel*
    // is the one deferred bit (see the ProgressBar in CloneDashboardView) — the values, cancel,
    // completion and error surfaced here are fully wired.
    private CancellationTokenSource? _cloneCts;

    [ObservableProperty]
    private bool _isCloning;

    /// <summary>Overall clone completion 0–100 (monotonic — driven by <see cref="ICloneService"/>).</summary>
    [ObservableProperty]
    private int _cloneProgressPercent;

    [ObservableProperty]
    private string _cloneStatusText = string.Empty;

    [ObservableProperty]
    private string? _cloneErrorText;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private ObservableCollection<GitHubRepository> _newRepositories = new();

    [ObservableProperty]
    private ObservableCollection<GitHubRepository> _existingRepositories = new();

    [ObservableProperty]
    private bool _hasExistingRepositories;

    [ObservableProperty]
    private GitHubRepository? _repoToConfirm;

    [ObservableProperty]
    private int _sortIndex = 0; // 0 = Recent, 1 = Alphabetical

    private List<GitHubRepository> _allRepos = new();

    partial void OnSortIndexChanged(int value)
    {
        ApplySorting();
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    private System.Threading.CancellationTokenSource? _pollingCts;
    public System.Action? CloseDeviceFlowDialogAction { get; set; }

    public System.Action<DeviceFlowResponse>? ShowDeviceFlowDialogAction { get; set; }
    public System.Action<GitHubRepository>? OnCloneRequested { get; set; }

    public CloneDashboardViewModel()
    {
        _authClient = new GitHubAuthClient();
        _keyring = new SecureKeyring();
        // Private HTTPS clones reuse the single-source credential resolver (token never in URL/argv).
        _cloneService = new CloneService(_keyring, new SshKeyService(_keyring));

        _ = CheckAuthenticationAsync();
    }

    /// <summary>
    /// Runs a clone with live progress into <see cref="CloneProgressPercent"/> / <see cref="CloneStatusText"/>,
    /// cancellable via <see cref="CancelCloneOperationCommand"/>. Deletes the partial directory on cancel
    /// (handled by <see cref="ICloneService"/>). Returns true on success.
    /// </summary>
    public async Task<bool> RunCloneAsync(string sourceUrl, string targetPath)
    {
        _cloneCts?.Cancel();
        _cloneCts = new CancellationTokenSource();
        IsCloning = true;
        CloneErrorText = null;
        CloneProgressPercent = 0;
        CloneStatusText = "Starting clone…";

        // Marshal progress onto the UI thread; the ICloneService reports from a background thread.
        var progress = new Progress<CloneProgress>(p =>
        {
            CloneProgressPercent = p.Percent;
            CloneStatusText = p.StatusText;
        });

        try
        {
            await _cloneService.CloneAsync(sourceUrl, targetPath, progress, _cloneCts.Token);
            CloneProgressPercent = 100;
            CloneStatusText = "Clone complete";
            return true;
        }
        catch (OperationCanceledException)
        {
            CloneStatusText = "Clone cancelled";
            return false;
        }
        catch (Exception ex)
        {
            CloneErrorText = ex.Message;
            CloneStatusText = "Clone failed";
            return false;
        }
        finally
        {
            IsCloning = false;
        }
    }

    [RelayCommand]
    private void CancelCloneOperation() => _cloneCts?.Cancel();

    private async Task CheckAuthenticationAsync()
    {
        var token = _keyring.RetrieveSecret("github_token");
        if (!string.IsNullOrEmpty(token))
        {
            IsAuthenticated = true;
            await LoadRepositoriesAsync(token);
        }
        else
        {
            IsAuthenticated = false;
        }
    }

    [RelayCommand]
    public void CancelLogin()
    {
        _pollingCts?.Cancel();
        IsLoading = false;
        StatusMessage = "Authentication failed.";
        CloseDeviceFlowDialogAction?.Invoke();
    }

    [RelayCommand]
    public async Task LoginAsync()
    {
        IsLoading = true;
        StatusMessage = "Starting device flow...";
        _pollingCts = new System.Threading.CancellationTokenSource();

        var deviceFlow = await _authClient.StartDeviceFlowAsync();
        if (deviceFlow != null)
        {
            // Show the code to the user
            ShowDeviceFlowDialogAction?.Invoke(deviceFlow);

            StatusMessage = "Waiting for authorization in browser...";

            // Start polling
            var token = await _authClient.PollForTokenAsync(deviceFlow, _pollingCts.Token);

            // Auto-close dialog if it was still open
            CloseDeviceFlowDialogAction?.Invoke();

            if (!string.IsNullOrEmpty(token) && !_pollingCts.Token.IsCancellationRequested)
            {
                _keyring.SaveSecret("github_token", token);
                // Also persist under the per-host key so GitService's multi-host
                // token lookup (TokenKeyForHost) resolves this token, not just the
                // legacy back-compat path.
                _keyring.SaveSecret(GitHostDetector.TokenKeyForHost("github.com"), token);
                IsAuthenticated = true;
                StatusMessage = "Authentication successful!";
                await LoadRepositoriesAsync(token);
            }
            else if (!_pollingCts.Token.IsCancellationRequested)
            {
                StatusMessage = "Authentication timed out or was denied.";
            }
        }
        else
        {
            StatusMessage = "Failed to start device flow. Check internet connection.";
        }

        IsLoading = false;
    }

    [RelayCommand]
    public void Logout()
    {
        _keyring.DeleteSecret("github_token");
        _keyring.DeleteSecret(GitHostDetector.TokenKeyForHost("github.com"));
        IsAuthenticated = false;
        NewRepositories.Clear();
        ExistingRepositories.Clear();
        StatusMessage = "Logged out successfully.";
    }

    private async Task LoadRepositoriesAsync(string token)
    {
        IsLoading = true;
        StatusMessage = "Fetching repositories...";

        var repos = await _authClient.GetUserRepositoriesAsync(token);
        _allRepos = repos;

        Dispatcher.UIThread.Post(() =>
        {
            ApplySorting();
            StatusMessage = $"Loaded {repos.Count} repositories.";
        });

        IsLoading = false;
    }

    private void ApplySorting()
    {
        var localUrls = new System.Collections.Generic.HashSet<string>();
        using (var db = new GitLoom.Core.AppDbContext())
        {
            foreach (var localRepo in db.Repositories)
            {
                localUrls.Add(localRepo.DisplayName.ToLowerInvariant());
            }
        }

        IEnumerable<GitHubRepository> sorted = _allRepos;
        if (SortIndex == 1) // Alphabetical
        {
            sorted = sorted.OrderBy(r => r.FullName.ToLowerInvariant());
        }
        else // Recent
        {
            sorted = sorted.OrderByDescending(r => r.UpdatedAt);
        }

        NewRepositories.Clear();
        ExistingRepositories.Clear();

        foreach (var repo in sorted)
        {
            repo.IsAddedLocally = localUrls.Contains(repo.Name.ToLowerInvariant());
            if (repo.IsAddedLocally)
                ExistingRepositories.Add(repo);
            else
                NewRepositories.Add(repo);
        }

        HasExistingRepositories = ExistingRepositories.Count > 0;
    }

    [RelayCommand]
    public void CloneRepository(GitHubRepository repo)
    {
        if (repo.IsAddedLocally)
        {
            RepoToConfirm = repo;
        }
        else
        {
            OnCloneRequested?.Invoke(repo);
        }
    }

    [RelayCommand]
    public void ConfirmClone()
    {
        if (RepoToConfirm != null)
        {
            OnCloneRequested?.Invoke(RepoToConfirm);
            RepoToConfirm = null;
        }
    }

    [RelayCommand]
    public void CancelClone()
    {
        RepoToConfirm = null;
    }
}
