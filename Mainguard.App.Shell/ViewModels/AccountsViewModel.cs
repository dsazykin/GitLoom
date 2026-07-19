using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Git.Security;
using Mainguard.Git.Sync;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// Accounts preferences page (T-14): one row per known Git host showing whether a
/// token is stored, the token-username convention, and how a token is acquired
/// (OAuth device flow for GitHub/GitLab, pasted PAT otherwise). Token storage stays
/// keyed <c>token_&lt;host&gt;</c> via <see cref="GitHostDetector.TokenKeyForHost"/>
/// (compat with the landed keyring). Constructed directly (no DI); the keyring is
/// injectable so the VM is unit-testable.
///
/// <para>Live device-flow / PAT-validation round trips are deferred to the T-14 manual
/// matrix; the offline paths (store / status / remove a PAT, provider resolution) are
/// fully exercised here.</para>
/// </summary>
public partial class AccountsViewModel : ViewModelBase
{
    private readonly ISecureKeyring _keyring;
    private readonly HostAuthContext _authContext;

    public ObservableCollection<AccountRowViewModel> Accounts { get; } = new();

    [ObservableProperty]
    private string _newHost = string.Empty;

    [ObservableProperty]
    private string? _statusMessage;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public AccountsViewModel(ISecureKeyring? keyring = null, HostAuthContext? authContext = null)
    {
        _keyring = keyring ?? new SecureKeyring();
        _authContext = authContext ?? HostAuthContext.Empty;
        Reload();
    }

    // The catalog of first-class hosts always shown; custom hosts append below.
    private static readonly (string Host, HostKind Kind)[] KnownHosts =
    {
        ("github.com", HostKind.GitHub),
        ("gitlab.com", HostKind.GitLab),
        ("bitbucket.org", HostKind.Bitbucket),
        ("dev.azure.com", HostKind.AzureDevOps),
    };

    private void Reload()
    {
        var previouslyCustom = new System.Collections.Generic.List<(string, HostKind)>();
        foreach (var row in Accounts)
            if (!IsKnown(row.Host))
                previouslyCustom.Add((row.Host, row.Kind));

        Accounts.Clear();
        foreach (var (host, kind) in KnownHosts)
            Accounts.Add(BuildRow(host, kind));
        foreach (var (host, kind) in previouslyCustom)
            Accounts.Add(BuildRow(host, kind));
    }

    private static bool IsKnown(string host)
    {
        foreach (var (h, _) in KnownHosts)
            if (string.Equals(h, host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private AccountRowViewModel BuildRow(string host, HostKind kind)
    {
        var provider = HostProviderRegistry.Resolve(host, kind, _authContext);
        var hasToken = !string.IsNullOrEmpty(_keyring.RetrieveSecret(GitHostDetector.TokenKeyForHost(host)))
            || (kind == HostKind.GitHub && !string.IsNullOrEmpty(_keyring.RetrieveSecret("github_token")));
        return new AccountRowViewModel(this, provider, hasToken);
    }

    private bool CanAddHost => !string.IsNullOrWhiteSpace(NewHost);
    partial void OnNewHostChanged(string value) => AddCustomHostCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanAddHost))]
    private void AddCustomHost()
    {
        var host = NewHost.Trim();
        var (_, kind) = GitHostDetector.Detect($"https://{host}/x/y.git");
        if (!IsKnown(host))
            Accounts.Add(BuildRow(host, kind));
        NewHost = string.Empty;
        StatusMessage = $"Added {host}. Sign in to store a token.";
    }

    /// <summary>Stores a pasted personal access token for a host (offline path — TI-14).</summary>
    internal void SaveToken(AccountRowViewModel row, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusMessage = "Paste a token first.";
            return;
        }
        _keyring.SaveSecret(GitHostDetector.TokenKeyForHost(row.Host), token.Trim());
        StatusMessage = $"Token stored for {row.Host}.";
        Reload();
    }

    /// <summary>Removes the stored token for a host.</summary>
    internal void SignOut(AccountRowViewModel row)
    {
        _keyring.DeleteSecret(GitHostDetector.TokenKeyForHost(row.Host));
        if (row.Kind == HostKind.GitHub) _keyring.DeleteSecret("github_token");
        StatusMessage = $"Signed out of {row.Host}.";
        Reload();
    }

    /// <summary>
    /// Acquires a token through the host provider (device flow or PAT prompt) and stores
    /// it. The live round trip is deferred to the manual matrix; the wiring is complete.
    /// </summary>
    internal async Task SignInAsync(AccountRowViewModel row, CancellationToken ct = default)
    {
        try
        {
            // TODO(T-14 human-review): live auth matrix — this drives the real device-flow /
            // PAT-validation network round trip.
            var token = await row.Provider.AcquireTokenAsync(ct);
            _keyring.SaveSecret(GitHostDetector.TokenKeyForHost(row.Host), token);
            StatusMessage = $"Signed in to {row.Host}.";
            Reload();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Sign-in to {row.Host} failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>One host row on the Accounts page: provider metadata + token status + per-row actions.</summary>
public partial class AccountRowViewModel : ViewModelBase
{
    private readonly AccountsViewModel _parent;

    public IHostProvider Provider { get; }
    public string Host => Provider.Host;
    public HostKind Kind => Provider.Kind;
    public HostAuthMethod AuthMethod => Provider.AuthMethod;
    public bool SupportsDeviceFlow => Provider.SupportsDeviceFlow;
    public string TokenUsername => Provider.TokenUsername;

    [ObservableProperty]
    private bool _hasToken;

    [ObservableProperty]
    private bool _isPatEntryVisible;

    [ObservableProperty]
    private string _patInput = string.Empty;

    public string AuthMethodLabel => AuthMethod switch
    {
        HostAuthMethod.OAuthDeviceFlow => "OAuth device flow",
        HostAuthMethod.OAuthLoopback => "OAuth (browser)",
        _ => "Personal access token",
    };
    public string StatusLabel => HasToken ? "Signed in" : "Not signed in";

    public AccountRowViewModel(AccountsViewModel parent, IHostProvider provider, bool hasToken)
    {
        _parent = parent;
        Provider = provider;
        _hasToken = hasToken;
    }

    [RelayCommand]
    private async Task SignIn()
    {
        // Any OAuth method (device flow OR loopback/browser) drives the provider's token acquisition;
        // only PAT hosts reveal the paste-a-token field.
        if (AuthMethod != HostAuthMethod.PersonalAccessToken)
            await _parent.SignInAsync(this);
        else
            IsPatEntryVisible = true; // reveal the paste-a-token field
    }

    [RelayCommand]
    private void SavePat()
    {
        _parent.SaveToken(this, PatInput);
        PatInput = string.Empty;
        IsPatEntryVisible = false;
    }

    [RelayCommand]
    private void CancelPat()
    {
        PatInput = string.Empty;
        IsPatEntryVisible = false;
    }

    [RelayCommand]
    private void SignOut() => _parent.SignOut(this);
}
