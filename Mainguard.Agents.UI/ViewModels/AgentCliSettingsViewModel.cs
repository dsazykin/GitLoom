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
public partial class AgentCliSettingsViewModel : ViewModelBase, ISettingsPage
{
    private readonly AgentCliInstaller? _installer;
    private readonly AgentCliUpdateService? _updater;
    private CancellationTokenSource? _cts;

    /// <summary>Live constructor: the real channel + VM install host, plus (optionally) the
    /// Mainguard-managed updater that annotates rows with newer registry releases and one-step revert.</summary>
    public AgentCliSettingsViewModel(AgentCliInstaller installer, AgentCliUpdateService? updater = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _updater = updater;
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
    [NotifyCanExecuteChangedFor(nameof(UpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand))]
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
            await AnnotateUpdatesAsync().ConfigureAwait(true);
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

    /// <summary>Annotates each row with a newer registry release (if any) and the version an accepted
    /// update replaced. Best-effort: an unreachable registry leaves rows unannotated, never an error —
    /// the window must stay fully useful offline.</summary>
    private async Task AnnotateUpdatesAsync()
    {
        if (_updater is null)
            return;
        try
        {
            var updates = await _updater.CheckForUpdatesAsync(CancellationToken.None).ConfigureAwait(true);
            foreach (var row in Clis)
            {
                row.UpdateAvailableVersion = updates.FirstOrDefault(u => u.Id == row.Id)?.LatestVersion;
                row.PreviousVersion = _updater.PreviousVersion(row.Id);
            }
        }
        catch (Exception)
        {
            // No registry, no annotations — install/refresh still work.
        }
    }

    /// <summary>Applies the row's offered update: download the exact new tarball, sha256-pin it, and
    /// install through the channel's verified path. The old pin becomes the row's Revert target.</summary>
    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAsync(AgentCliRowViewModel row)
    {
        if (_updater is null || row.UpdateAvailableVersion is not { Length: > 0 } target)
            return;
        await RunPinMoveAsync(row,
            $"Updating to v{target} — downloading, verifying, and installing…",
            ct => _updater.ApplyUpdateAsync(row.Id, target, ct)).ConfigureAwait(true);
    }

    private bool CanUpdate(AgentCliRowViewModel? row) => !IsBusy && row is { HasUpdate: true };

    /// <summary>Reverts the row to the version its last accepted update replaced — the escape hatch
    /// when a new CLI release breaks the app.</summary>
    [RelayCommand(CanExecute = nameof(CanRevert))]
    private async Task RevertAsync(AgentCliRowViewModel row)
    {
        if (_updater is null || row.PreviousVersion is not { Length: > 0 } target)
            return;
        await RunPinMoveAsync(row,
            $"Reverting to v{target}…",
            ct => _updater.RevertAsync(row.Id, ct)).ConfigureAwait(true);
    }

    private bool CanRevert(AgentCliRowViewModel? row) => !IsBusy && row is { HasPrevious: true };

    /// <summary>The shared update/revert body: serialized like installs, failure-isolated to the row,
    /// and finished with a full refresh so version chips and annotations always tell the truth.</summary>
    private async Task RunPinMoveAsync(AgentCliRowViewModel row, string progress, Func<CancellationToken, Task> move)
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        row.IsFailed = false;
        row.IsInstalling = true;
        row.StatusMessage = progress;
        try
        {
            await move(_cts.Token).ConfigureAwait(true);
            row.StatusMessage = null;
            row.IsInstalling = false;
            IsBusy = false;
            await RefreshAsync().ConfigureAwait(true);
            return;
        }
        catch (OperationCanceledException)
        {
            row.StatusMessage = "Cancelled. The previously installed version is unchanged.";
        }
        catch (Exception ex)
        {
            row.IsFailed = true;
            row.StatusMessage = $"{row.Id} could not be switched: {ex.Message} The previous pin was kept.";
        }
        finally
        {
            row.IsInstalling = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();

    /// <summary>As a Settings page: kick the initial catalog read the moment this page becomes
    /// visible (replaces the old Window.OnDataContextChanged-triggered refresh).</summary>
    public void OnActivated()
    {
        if (RefreshCommand.CanExecute(null))
            RefreshCommand.Execute(null);
    }

    public void OnDeactivated()
    {
    }
}
