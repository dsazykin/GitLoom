using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Models;
using GitLoom.Core.Security;
using GitLoom.Core.Sync;
using LibGit2Sharp;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace GitLoom.App.ViewModels
{
    public partial class CloneDashboardViewModel : ViewModelBase
    {
        private readonly GitHubAuthClient _authClient;
        private readonly SecureKeyring _keyring;

        [ObservableProperty]
        private bool _isAuthenticated;

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private ObservableCollection<GitHubRepository> _cloudRepositories = new();

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

            _ = CheckAuthenticationAsync();
        }

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
            StatusMessage = "Login cancelled.";
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
            IsAuthenticated = false;
            CloudRepositories.Clear();
            StatusMessage = "Logged out successfully.";
        }

        private async Task LoadRepositoriesAsync(string token)
        {
            IsLoading = true;
            StatusMessage = "Fetching repositories...";

            var repos = await _authClient.GetUserRepositoriesAsync(token);
            
            // Check local DB for already cloned repos
            var localUrls = new System.Collections.Generic.HashSet<string>();
            using (var db = new GitLoom.Core.AppDbContext())
            {
                foreach(var localRepo in db.Repositories)
                {
                    localUrls.Add(localRepo.DisplayName.ToLowerInvariant());
                }
            }
            
            Dispatcher.UIThread.Post(() =>
            {
                CloudRepositories.Clear();
                foreach (var repo in repos.OrderByDescending(r => r.UpdatedAt))
                {
                    repo.IsAddedLocally = localUrls.Contains(repo.Name.ToLowerInvariant());
                    CloudRepositories.Add(repo);
                }
                StatusMessage = $"Loaded {repos.Count} repositories.";
            });

            IsLoading = false;
        }

        [RelayCommand]
        public void CloneRepository(GitHubRepository repo)
        {
            OnCloneRequested?.Invoke(repo);
        }
    }
}
