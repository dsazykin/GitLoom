using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core;
using GitLoom.Core.Exceptions;
using GitLoom.Core.Security;

namespace GitLoom.App.ViewModels;

/// <summary>
/// AI Providers preferences page (P2-01): store a BYOK LLM API key per provider, keyed
/// <c>llm_&lt;provider&gt;</c> in the OS keyring. Save is <b>validate-then-store</b> — the key is
/// health-checked off the UI thread first (<see cref="ApiKeyHealthService"/>) and an invalid key is
/// never persisted (invariant 3). The candidate key lives in a plain string only while the page is open
/// and is nulled after every save/health-check (invariant 1). The CLI-OAuth path shows the Anthropic
/// ToS notice before it can activate.
///
/// <para>Constructed directly (no DI). The keyring, the health-check delegate, and the db factory are
/// injectable seams so the VM is fully unit-testable offline.</para>
/// </summary>
public partial class ApiKeySettingsViewModel : ViewModelBase
{
    private readonly ISecureKeyStore _keyStore;
    private readonly Func<string, string, CancellationToken, Task<KeyHealth>> _healthCheck;
    private readonly Func<AppDbContext> _dbFactory;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The providers offered in the dropdown. Static array so <c>llm_&lt;provider&gt;</c> extends
    /// without UI rework.</summary>
    public string[] AvailableProviders { get; } = { "anthropic", "openai" };

    public ObservableCollection<ApiKeyProviderRowViewModel> Providers { get; } = new();

    [ObservableProperty]
    private string _selectedProvider = "anthropic";

    [ObservableProperty]
    private string _apiKey = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _healthMessage;

    [ObservableProperty]
    private bool _isHealthError;

    [ObservableProperty]
    private bool _isCliOAuthEnabled;

    /// <summary>Set by the View to present the modal ToS dialog; returns true when acknowledged.</summary>
    public Func<string, Task<bool>>? ShowTosDialogAsync { get; set; }

    /// <summary>Wired from the View so Close works from the ViewModel.</summary>
    public Action? CloseAction { get; set; }

    public ApiKeySettingsViewModel(
        ISecureKeyStore? keyStore = null,
        Func<string, string, CancellationToken, Task<KeyHealth>>? healthCheck = null,
        Func<AppDbContext>? dbFactory = null)
    {
        _keyStore = keyStore ?? new SecureKeyring();
        _healthCheck = healthCheck ?? new ApiKeyHealthService().CheckAsync;
        _dbFactory = dbFactory ?? (() => new AppDbContext());
        RefreshRows();
    }

    private void RefreshRows()
    {
        Providers.Clear();
        foreach (var provider in AvailableProviders)
        {
            var hasKey = !string.IsNullOrEmpty(_keyStore.Get($"llm_{provider}"));
            Providers.Add(new ApiKeyProviderRowViewModel(this, provider, hasKey));
        }
    }

    private bool CanSave => !IsBusy && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SelectedProvider);
    partial void OnIsBusyChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();
    partial void OnApiKeyChanged(string value) => SaveCommand.NotifyCanExecuteChanged();

    /// <summary>Validate-then-store: health-check off the UI thread; store only when valid; then null the
    /// candidate key. A transport/unknown-provider failure surfaces inline and stores nothing.</summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task Save()
    {
        var provider = SelectedProvider;
        var key = ApiKey; // captured before the field is cleared
        IsBusy = true;
        IsHealthError = false;
        HealthMessage = "Checking key…";
        try
        {
            var health = await Task.Run(() => _healthCheck(provider, key, _cts.Token), _cts.Token);
            if (health.IsValid)
            {
                _keyStore.Set($"llm_{provider}", key);
                IsHealthError = false;
                HealthMessage = $"Key valid — supports ~{health.EstimatedConcurrentAgents} concurrent agents.";
                RefreshRows();
            }
            else
            {
                // Invalid key is NOT stored (invariant 3).
                IsHealthError = true;
                HealthMessage = health.FailureReason ?? "The provider rejected this key.";
            }
        }
        catch (OperationCanceledException)
        {
            // Page closed mid-check — nothing stored, no message churn.
        }
        catch (GitLoomException ex)
        {
            // Unreachable / unknown provider: typed failure — nothing stored, retry affordance stays.
            IsHealthError = true;
            HealthMessage = ex.Message;
        }
        catch (Exception ex)
        {
            IsHealthError = true;
            HealthMessage = ex.Message;
        }
        finally
        {
            ApiKey = string.Empty; // null out the candidate after the check completes (invariant 1)
            IsBusy = false;
        }
    }

    internal void DeleteKey(string provider)
    {
        _keyStore.Delete($"llm_{provider}");
        IsHealthError = false;
        HealthMessage = $"Removed the stored {provider} key.";
        RefreshRows();
    }

    /// <summary>CLI-OAuth path: show the Anthropic ToS notice (unless already acknowledged) before it can
    /// activate. Cancel leaves the option off.</summary>
    [RelayCommand]
    private async Task UseClaudeSubscription()
    {
        const string provider = "anthropic";
        bool acknowledged;
        using (var db = _dbFactory())
            acknowledged = db.HasTosAcknowledgment(provider);

        if (!acknowledged)
        {
            acknowledged = ShowTosDialogAsync is not null && await ShowTosDialogAsync(provider);
            if (!acknowledged)
            {
                IsHealthError = false;
                HealthMessage = "Claude subscription (CLI OAuth) was not enabled.";
                return;
            }
        }

        IsCliOAuthEnabled = true;
        IsHealthError = false;
        HealthMessage = "Using your Claude subscription (CLI OAuth). The API-key path stays recommended.";
    }

    /// <summary>Called by the View when the page closes: cancel any in-flight health check.</summary>
    public void CancelPendingWork()
    {
        try { _cts.Cancel(); } catch { /* already disposed */ }
    }

    [RelayCommand]
    private void Close()
    {
        CancelPendingWork();
        CloseAction?.Invoke();
    }
}

/// <summary>One provider row on the AI Providers page: stored-key status + a per-provider delete.</summary>
public partial class ApiKeyProviderRowViewModel : ViewModelBase
{
    private readonly ApiKeySettingsViewModel _parent;

    public string Provider { get; }
    public string DisplayName { get; }

    [ObservableProperty]
    private bool _hasKey;

    public ApiKeyProviderRowViewModel(ApiKeySettingsViewModel parent, string provider, bool hasKey)
    {
        _parent = parent;
        Provider = provider;
        DisplayName = provider switch
        {
            "anthropic" => "Anthropic (Claude)",
            "openai" => "OpenAI",
            _ => provider,
        };
        _hasKey = hasKey;
    }

    public string StatusLabel => HasKey ? "Key stored" : "No key stored";

    [RelayCommand]
    private void Delete() => _parent.DeleteKey(Provider);
}
