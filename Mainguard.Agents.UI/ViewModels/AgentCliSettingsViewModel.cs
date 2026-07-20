using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Adapters;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The "add more later" surface for agent CLIs (P2-22 §J-5): lists what the pinned starter channel
/// offers with each CLI's live install/health state (a version-matched probe inside the VM — the same
/// check the channel's idempotence uses, so this list never lies), and installs more on demand. The
/// same <see cref="AgentCliInstaller"/> the OOBE picker drives; a CLI installed here reaches every
/// NEW agent sandbox immediately via the read-only adapters mount — no image rebuild, no re-setup.
///
/// <para>Installs are serialized (the shared npm prefix must never see two concurrent installs) and
/// failure-isolated: a failing row shows its actionable cause on itself and nothing else is touched.
/// Constructed directly (no DI); the installer is an injectable seam for tests/harness.</para>
/// </summary>
public partial class AgentCliSettingsViewModel : ViewModelBase
{
    private readonly AgentCliInstaller? _installer;
    private CancellationTokenSource? _cts;

    /// <summary>Live constructor: the real channel + VM install host.</summary>
    public AgentCliSettingsViewModel(AgentCliInstaller installer)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
    }

    /// <summary>Design/render constructor: fixed representative rows, no service behind them.</summary>
    public AgentCliSettingsViewModel(IEnumerable<AgentCliRowViewModel> rows, bool isLoading = false, string? loadError = null)
    {
        foreach (var row in rows)
            Clis.Add(row);
        _isLoading = isLoading;
        _loadError = loadError;
    }

    public ObservableCollection<AgentCliRowViewModel> Clis { get; } = new();

    /// <summary>True while the channel + per-CLI probes are read — a named "checking" line shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    private string? _loadError;

    public bool HasLoadError => !string.IsNullOrEmpty(LoadError);

    /// <summary>An install is in flight — every row's Install disables (serialized installs).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>Loaded fine, channel just offers nothing (a future channel state; honest empty).</summary>
    public bool ShowEmpty => !IsLoading && !HasLoadError && Clis.Count == 0;

    /// <summary>Wired from the View so Close works from the ViewModel.</summary>
    public Action? CloseAction { get; set; }

    /// <summary>Initial load and the "Refresh" action: re-reads the channel offer and re-probes each
    /// CLI's installed state inside the VM.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        if (_installer is null)
            return; // design/render instance
        LoadError = null;
        IsLoading = true;
        try
        {
            var options = await _installer.ListAsync(CancellationToken.None).ConfigureAwait(true);
            Clis.Clear();
            foreach (var option in options)
                Clis.Add(new AgentCliRowViewModel(option));
        }
        catch (Exception ex)
        {
            LoadError = $"Mainguard could not read its agent-CLI catalog: {ex.Message} "
                + "If the Mainguard environment is not running, open Mainguard again to start it, then Refresh.";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(ShowEmpty));
        }
    }

    private bool CanRefresh() => !IsBusy;

    /// <summary>Installs one CLI at its pinned version (fetch → sha256-verify → in-VM install →
    /// version-matched probe). The row carries its own progress and, on failure, the actionable cause.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync(AgentCliRowViewModel row)
    {
        if (_installer is null || row.IsInstalled || row.IsInstalling)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        row.IsFailed = false;
        row.IsInstalling = true;
        row.StatusMessage = "Downloading, verifying, and installing — this can take a few minutes "
            + "on a slow connection.";
        try
        {
            var outcomes = await _installer
                .InstallAsync(new[] { row.Id }, progress: null, _cts.Token).ConfigureAwait(true);
            var outcome = outcomes[0];
            if (outcome.Succeeded)
            {
                row.IsInstalled = true;
                row.StatusMessage = null;
            }
            else
            {
                row.IsFailed = true;
                row.StatusMessage = outcome.Error;
            }
        }
        catch (OperationCanceledException)
        {
            row.StatusMessage = "Cancelled. Nothing else was changed — you can install it again anytime.";
        }
        finally
        {
            row.IsInstalling = false;
            IsBusy = false;
        }
    }

    private bool CanInstall(AgentCliRowViewModel? row) => !IsBusy && row is { CanInstall: true };

    /// <summary>Aborts the in-flight install; the row reports the cancellation on itself.</summary>
    [RelayCommand]
    private void CancelInstall() => _cts?.Cancel();

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}
