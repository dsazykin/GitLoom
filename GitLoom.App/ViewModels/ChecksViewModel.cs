using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Git.Models;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// CI / Checks panel (T-26), sibling of the Issues/PR panels: for one commit it shows the overall status
/// badge (✓ / ✕ / pending, hidden when the commit has no checks) plus every check run — name, per-run
/// state icon, "view logs" (opens <c>DetailsUrl</c> in the browser) and a per-run Re-run. When the origin
/// host is unsupported or no token is stored it shows a graceful sign-in / unsupported affordance instead
/// of erroring.
///
/// <para>All network work runs inside the async <see cref="ICheckStatusService"/> (off the UI thread) and
/// is gated by <see cref="IsBusy"/>; the bound <see cref="Runs"/> collection and <see cref="Badge"/> are
/// only ever mutated on the <see cref="Dispatcher.UIThread"/>. Hosted by ChecksWindow.</para>
/// </summary>
public partial class ChecksViewModel : ViewModelBase
{
    private readonly ICheckStatusService _checks;
    private readonly string _repoPath;
    private readonly string _sha;
    private readonly Action<string> _openUrl;
    private CancellationTokenSource? _cts;

    public ObservableCollection<CheckRunRowViewModel> Runs { get; } = new();

    /// <summary>The rolled-up overall badge for the commit; hidden when the commit has no checks.</summary>
    [ObservableProperty]
    private CheckBadgeViewModel _badge = CheckBadgeViewModel.Empty;

    [ObservableProperty]
    private bool _isSupported;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>True once a load has completed and the commit reported no checks at all (no CI configured).</summary>
    [ObservableProperty]
    private bool _isEmpty;

    /// <summary>Shown when <see cref="IsSupported"/> is false — the unsupported-host / sign-in affordance text.</summary>
    [ObservableProperty]
    private string _unsupportedHint = "";

    /// <summary>The short commit sha shown in the header.</summary>
    public string ShortSha => _sha.Length >= 7 ? _sha.Substring(0, 7) : _sha;

    // Wired from the View so Close works from the ViewModel.
    public Action? CloseAction { get; set; }

    public ChecksViewModel(ICheckStatusService checks, string repoPath, string sha, Action<string>? openUrl = null)
    {
        _checks = checks;
        _repoPath = repoPath;
        _sha = sha ?? "";
        _openUrl = openUrl ?? Services.BrowserLauncher.OpenUrl;

        IsSupported = SafeIsSupported();
        if (!IsSupported)
        {
            UnsupportedHint =
                "CI checks aren't available for this repository yet. Connect an account for the origin host " +
                "(GitHub is supported today) from Accounts, then reopen this panel.";
        }
    }

    private bool SafeIsSupported()
    {
        try { return _checks.IsSupported(_repoPath); }
        catch { return false; }
    }

    // ---- Load --------------------------------------------------------------------------------

    private bool CanRefresh => IsSupported && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task Refresh()
    {
        if (!IsSupported || IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var result = await _checks.GetChecksAsync(_repoPath, _sha, ct);
            await ApplyOnUiAsync(() =>
            {
                Runs.Clear();
                foreach (var run in result.Runs)
                    Runs.Add(new CheckRunRowViewModel(run, this));
                Badge = CheckBadgeViewModel.FromChecks(result);
                IsEmpty = !result.HasAny;
            });
        }
        catch (OperationCanceledException) { /* superseded by a newer refresh — ignore */ }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Per-run actions ---------------------------------------------------------------------

    internal async Task RerunAsync(CheckRunRowViewModel row)
    {
        if (IsBusy || !row.CanRerun) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await _checks.RerequestAsync(_repoPath, row.Id, CancellationToken.None);
        }
        catch (Exception ex)
        {
            await ApplyOnUiAsync(() => ErrorMessage = ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (ErrorMessage is null)
            await Refresh();
    }

    internal void OpenLogs(CheckRunRowViewModel row)
    {
        if (!string.IsNullOrWhiteSpace(row.DetailsUrl))
            _openUrl(row.DetailsUrl);
    }

    // ---- Plumbing ----------------------------------------------------------------------------

    partial void OnIsBusyChanged(bool value) => RefreshCommand.NotifyCanExecuteChanged();
    partial void OnIsSupportedChanged(bool value) => RefreshCommand.NotifyCanExecuteChanged();

    // Applies a mutation to bound state on the UI thread (invariant G-5): never mutates the observable
    // collection off-thread. Runs inline when already on the UI thread.
    private static Task ApplyOnUiAsync(Action apply)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            apply();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(apply).GetTask();
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}

/// <summary>
/// One check-run row (T-26): name + state icon, "view logs" (opens the run's details URL) and — for a
/// re-requestable check-run (a legacy commit status has no id) — a Re-run action, both routed back through
/// the parent. The state is exposed as mutually-exclusive booleans so the View picks a design-token-styled
/// icon (no color logic in the VM).
/// </summary>
public partial class CheckRunRowViewModel : ViewModelBase
{
    private readonly ChecksViewModel _parent;

    public long Id { get; }
    public string Name { get; }
    public CheckState State { get; }
    public string DetailsUrl { get; }
    public bool CanRerun { get; }
    public DateTimeOffset? CompletedAt { get; }

    public CheckRunRowViewModel(CheckRunItem item, ChecksViewModel parent)
    {
        _parent = parent;
        Id = item.Id;
        Name = string.IsNullOrEmpty(item.Name) ? "(unnamed check)" : item.Name;
        State = item.State;
        DetailsUrl = item.DetailsUrl;
        CanRerun = item.CanRerun;
        CompletedAt = item.CompletedAt;
    }

    public bool IsSuccess => State == CheckState.Success;
    public bool IsFailure => State == CheckState.Failure;
    public bool IsPending => State == CheckState.Pending;
    public bool IsNeutral => State == CheckState.Neutral;

    public string StateText => State switch
    {
        CheckState.Success => "passed",
        CheckState.Failure => "failed",
        CheckState.Pending => "in progress",
        _ => "skipped",
    };

    public bool HasUrl => !string.IsNullOrWhiteSpace(DetailsUrl);
    public string WhenText => CompletedAt is { } c && c != default ? c.LocalDateTime.ToString("MMM d, HH:mm") : "";

    [RelayCommand]
    private void OpenLogs() => _parent.OpenLogs(this);

    [RelayCommand]
    private Task Rerun() => _parent.RerunAsync(this);
}

/// <summary>
/// The compact overall status badge (T-26) — the one reused on the Checks panel header and, when wired,
/// on a commit's detail area. Carries the rolled-up <see cref="CheckState"/> as mutually-exclusive
/// booleans (the View selects a Success/Danger/Warning-token icon; the VM holds no brushes) plus a compact
/// "✓ n · ✕ n · • n" summary. <see cref="IsVisible"/> is false when the commit has no checks, so the badge
/// disappears entirely rather than reading as a spurious state.
/// </summary>
public sealed class CheckBadgeViewModel
{
    public CheckState State { get; }
    public bool IsVisible { get; }
    public int Passed { get; }
    public int Failed { get; }
    public int Pending { get; }

    private CheckBadgeViewModel(CheckState state, bool visible, int passed, int failed, int pending)
    {
        State = state;
        IsVisible = visible;
        Passed = passed;
        Failed = failed;
        Pending = pending;
    }

    /// <summary>A hidden, stateless badge (no checks loaded / no CI).</summary>
    public static CheckBadgeViewModel Empty { get; } = new(CheckState.Success, false, 0, 0, 0);

    public static CheckBadgeViewModel FromChecks(CommitChecks checks) =>
        checks.HasAny
            ? new CheckBadgeViewModel(checks.Overall, true, checks.Passed, checks.Failed, checks.Pending)
            : Empty;

    public bool IsSuccess => State == CheckState.Success;
    public bool IsFailure => State == CheckState.Failure;
    public bool IsPending => State == CheckState.Pending;

    public string GlyphText => State switch
    {
        CheckState.Failure => "✕",
        CheckState.Pending => "•",
        _ => "✓",
    };

    public string SummaryText => State switch
    {
        CheckState.Failure => $"{Failed} failing",
        CheckState.Pending => $"{Pending} in progress",
        _ => Failed > 0 || Pending > 0 ? $"{Passed} passed" : "All checks passed",
    };

    /// <summary>A compact per-state count line for the panel header: "✓ 3 · ✕ 1 · • 2".</summary>
    public string CountsText => $"✓ {Passed} · ✕ {Failed} · • {Pending}";
}
