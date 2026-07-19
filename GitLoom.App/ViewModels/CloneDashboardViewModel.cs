using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;
using Mainguard.Git.Sync;

namespace GitLoom.App.ViewModels;

/// <summary>
/// Clone Dashboard (T-21, generalized in P2-48 for multi-provider). Lists the signed-in account's
/// repositories for a selected host and clones the chosen one. The listing/clone are host-agnostic:
/// repos come from <see cref="IHostRepositoryService"/> as <see cref="RemoteRepository"/> (GitHub +
/// GitLab today) and the clone credential is resolved per-host by <c>ICloneService</c>/<c>CredentialResolver</c>
/// (keyring key <c>token_&lt;host&gt;</c>), so no path is hardcoded to GitHub anymore. The provider
/// selector is driven by which known hosts the user is signed into (GitLab appears once a token is stored
/// via the Accounts screen). GitHub's own in-screen device-flow sign-in is preserved unchanged.
/// </summary>
public partial class CloneDashboardViewModel : ViewModelBase
{
    // The catalog of first-class hosts probed for a stored token; mirrors AccountsViewModel.KnownHosts.
    private static readonly (string Host, HostKind Kind)[] KnownHosts =
    {
        ("github.com", HostKind.GitHub),
        ("gitlab.com", HostKind.GitLab),
        ("bitbucket.org", HostKind.Bitbucket),
        ("dev.azure.com", HostKind.AzureDevOps),
    };

    private readonly GitHubAuthClient _authClient;
    private readonly SecureKeyring _keyring;
    private readonly ICloneService _cloneService;
    private readonly IHostRepositoryService _repoService;

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

    /// <summary>The signed-in hosts the user can list/clone from (GitHub / GitLab today).</summary>
    [ObservableProperty]
    private ObservableCollection<CloneProviderOption> _providers = new();

    /// <summary>The provider whose repositories are shown; changing it reloads the list.</summary>
    [ObservableProperty]
    private CloneProviderOption? _selectedProvider;

    /// <summary>True when more than one provider is signed in — the segmented selector is only shown then.</summary>
    [ObservableProperty]
    private bool _hasProviderSelector;

    [ObservableProperty]
    private ObservableCollection<RemoteRepository> _newRepositories = new();

    [ObservableProperty]
    private ObservableCollection<RemoteRepository> _existingRepositories = new();

    [ObservableProperty]
    private bool _hasExistingRepositories;

    [ObservableProperty]
    private RemoteRepository? _repoToConfirm;

    [ObservableProperty]
    private int _sortIndex = 0; // 0 = Recent, 1 = Alphabetical

    private List<RemoteRepository> _allRepos = new();
    private CancellationTokenSource? _loadCts;

    partial void OnSortIndexChanged(int value)
    {
        ApplySorting();
    }

    partial void OnSelectedProviderChanged(CloneProviderOption? value)
    {
        foreach (var p in Providers)
            p.IsSelected = ReferenceEquals(p, value);
        if (value is not null)
            _ = LoadRepositoriesAsync();
    }

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    private System.Threading.CancellationTokenSource? _pollingCts;
    public System.Action? CloseDeviceFlowDialogAction { get; set; }

    public System.Action<DeviceFlowResponse>? ShowDeviceFlowDialogAction { get; set; }
    public System.Action<RemoteRepository>? OnCloneRequested { get; set; }

    /// <param name="keyring">Token store; tests inject a temp-dir keyring.</param>
    /// <param name="repoService">Host-agnostic repo lister; tests inject one over a fixture HttpClient.</param>
    public CloneDashboardViewModel(ISecureKeyring? keyring = null, IHostRepositoryService? repoService = null)
    {
        _keyring = keyring as SecureKeyring ?? new SecureKeyring();
        _authClient = new GitHubAuthClient();
        // Private HTTPS clones reuse the single-source credential resolver (token never in URL/argv),
        // which resolves the per-host token (token_<host>) for whichever provider the repo came from.
        _cloneService = new CloneService(_keyring, new SshKeyService(_keyring));
        _repoService = repoService ?? new HostRepositoryService(_keyring);

        RefreshProviders();
    }

    /// <summary>
    /// Recomputes the signed-in provider list from the keyring (a host appears only when it has an
    /// implemented lister AND a stored token), preserving the current selection where possible. Public
    /// so a caller that opens a separate sign-in surface (the Client first-run's Accounts window) can
    /// refresh the selector when a new host is signed in there.
    /// </summary>
    public void RefreshProviders()
    {
        var previousHost = SelectedProvider?.Host;

        var signedIn = KnownHosts
            .Where(h => _repoService.IsSupported(h.Host, h.Kind))
            .Select(h => new CloneProviderOption(h.Host, h.Kind))
            .ToList();

        Providers = new ObservableCollection<CloneProviderOption>(signedIn);
        HasProviderSelector = signedIn.Count > 1;
        IsAuthenticated = signedIn.Count > 0;

        if (signedIn.Count == 0)
        {
            SelectedProvider = null;
            _allRepos = new();
            NewRepositories.Clear();
            ExistingRepositories.Clear();
            HasExistingRepositories = false;
            return;
        }

        // Re-select the same host if it's still signed in, else the first provider.
        SelectedProvider = signedIn.FirstOrDefault(p => p.Host == previousHost) ?? signedIn[0];
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

    /// <summary>Switches the active provider (segmented selector); reloads its repositories.</summary>
    [RelayCommand]
    private void SelectProvider(CloneProviderOption? option)
    {
        if (option is not null)
            SelectedProvider = option;
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
                // Persist under the per-host key so GitService's multi-host token lookup
                // (TokenKeyForHost) resolves this token; keep the legacy github_token for back-compat.
                _keyring.SaveSecret("github_token", token);
                _keyring.SaveSecret(GitHostDetector.TokenKeyForHost("github.com"), token);
                StatusMessage = "Authentication successful!";
                RefreshProviders();
                // Make sure GitHub is the visible provider right after signing in.
                SelectedProvider = Providers.FirstOrDefault(p => p.Kind == HostKind.GitHub) ?? SelectedProvider;
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

    /// <summary>Signs out of the currently selected provider (removes its stored token) and refreshes.</summary>
    [RelayCommand]
    public void Logout()
    {
        var provider = SelectedProvider;
        if (provider is null) return;

        _keyring.DeleteSecret(GitHostDetector.TokenKeyForHost(provider.Host));
        if (provider.Kind == HostKind.GitHub)
            _keyring.DeleteSecret("github_token");

        StatusMessage = $"Signed out of {provider.DisplayName}.";
        RefreshProviders();
    }

    private async Task LoadRepositoriesAsync()
    {
        var provider = SelectedProvider;
        if (provider is null) return;

        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        IsLoading = true;
        StatusMessage = "Fetching repositories...";

        try
        {
            var repos = await _repoService.ListMyRepositoriesAsync(provider.Host, provider.Kind, ct);
            if (ct.IsCancellationRequested) return;
            _allRepos = repos.ToList();

            Dispatcher.UIThread.Post(() =>
            {
                ApplySorting();
                StatusMessage = $"Loaded {_allRepos.Count} repositories.";
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer load (provider switch) — leave the newer load to update state.
            return;
        }
        catch (Exception ex)
        {
            _allRepos = new();
            Dispatcher.UIThread.Post(() =>
            {
                ApplySorting();
                StatusMessage = $"Could not load repositories: {ex.Message}";
            });
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private void ApplySorting()
    {
        var localUrls = new System.Collections.Generic.HashSet<string>();
        try
        {
            using var db = new Mainguard.Git.AppDbContext();
            foreach (var localRepo in db.Repositories)
            {
                localUrls.Add(localRepo.DisplayName.ToLowerInvariant());
            }
        }
        catch
        {
            // A momentarily unavailable local DB just means "nothing known to be cloned yet" —
            // every repo is then offered as new rather than crashing the clone screen.
        }

        IEnumerable<RemoteRepository> sorted = _allRepos;
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
    public void CloneRepository(RemoteRepository repo)
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

/// <summary>
/// One signed-in host offered by the Clone Dashboard's provider selector (P2-48). Carries the host +
/// kind used to list/clone and the label/selection state the segmented control binds to.
/// </summary>
public partial class CloneProviderOption : ObservableObject
{
    public string Host { get; }
    public HostKind Kind { get; }

    [ObservableProperty]
    private bool _isSelected;

    public CloneProviderOption(string host, HostKind kind)
    {
        Host = host;
        Kind = kind;
    }

    /// <summary>Human label for the segment (e.g. "GitHub", "GitLab"); a custom host shows its hostname.</summary>
    public string DisplayName => Kind switch
    {
        HostKind.GitHub => "GitHub",
        HostKind.GitLab => "GitLab",
        HostKind.Bitbucket => "Bitbucket",
        HostKind.AzureDevOps => "Azure DevOps",
        _ => Host,
    };

    /// <summary>
    /// App.axaml StreamGeometry resource key for this provider's header logo. Bitbucket/Azure DevOps/
    /// custom hosts have no dedicated logo in the icon set yet, so they deliberately fall back to the
    /// generic GitHub octocat until their own logos are added.
    /// </summary>
    public string IconKey => Kind switch
    {
        HostKind.GitHub => "GitHubIcon",
        HostKind.GitLab => "GitLabIcon",
        _ => "GitHubIcon",
    };
}
