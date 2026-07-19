using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The repo-onboarding engine — the ONE "copy host repositories into GitLoom OS" flow, extracted
/// from the OOBE wizard's repo step so the post-setup surface (Tools → "Add Repos to GitLoom OS…",
/// <see cref="AddReposToOsViewModel"/>) drives the identical logic instead of a copy. Two ways in
/// (one scanned folder — persisted to the EXISTING <c>UserPreferences.AutoDetectPath</c> — or
/// individually validated picks), a default-checked <see cref="OnboardRepoRowViewModel"/> list,
/// then a sequential copy run that is failure-isolated per row (a failing repo lands its actionable
/// cause on ITS row and the rest still copy) and cancellable without stranding anything. Copying an
/// already-provisioned repo succeeds quietly — the daemon's <c>ProvisionRepo</c> and the sync-remote
/// registration are both idempotent, so re-copying can never duplicate.
///
/// <para>Every side-effecting seam is injected (discovery walk, folder pickers, the per-repo
/// provision pipeline, the sidebar repo store, settings) so the flow is unit-testable with fakes.
/// Null seams are tolerated: the affected command is a safe no-op, exactly as the wizard behaved
/// before extraction (<see cref="CanOnboard"/> tells hosts whether the flow can function at all).
/// All bindable state mutates only on the caller's (UI) thread — the awaits use
/// <c>ConfigureAwait(true)</c> and the commands are only ever invoked from bindings.</para>
/// </summary>
public partial class RepoOnboardingViewModel : ViewModelBase
{
    private readonly IRepoDiscoveryService? _repoDiscovery;
    private readonly Func<Task<string?>>? _pickRootFolder;
    private readonly Func<Task<IReadOnlyList<string>>>? _pickIndividualFolders;
    private readonly Func<string, CancellationToken, Task>? _provisionRepo;
    private readonly Action<string>? _persistRepo;
    private readonly ISettingsService? _settingsService;
    private CancellationTokenSource? _cts;

    /// <summary>Live constructor. Null seams are tolerated (tests / no daemon): the commands touching
    /// them no-op and <see cref="CanOnboard"/> is false when the flow cannot function.</summary>
    public RepoOnboardingViewModel(
        IRepoDiscoveryService? repoDiscovery,
        Func<Task<string?>>? pickRootFolder,
        Func<Task<IReadOnlyList<string>>>? pickIndividualFolders,
        Func<string, CancellationToken, Task>? provisionRepo,
        Action<string>? persistRepo = null,
        ISettingsService? settingsService = null)
    {
        _repoDiscovery = repoDiscovery;
        _pickRootFolder = pickRootFolder;
        _pickIndividualFolders = pickIndividualFolders;
        _provisionRepo = provisionRepo;
        _persistRepo = persistRepo;
        _settingsService = settingsService;
    }

    /// <summary>Design/render constructor: fixed representative state, no live seams (every command
    /// is an inert no-op).</summary>
    public RepoOnboardingViewModel(
        IEnumerable<OnboardRepoRowViewModel>? rows = null,
        bool isProvisioning = false,
        bool isScanning = false,
        string? notice = null)
    {
        if (rows is not null)
            foreach (var r in rows)
                AttachRepoRow(r);
        _isProvisioningRepos = isProvisioning;
        _isRepoScanning = isScanning;
        _repoNotice = notice;
    }

    /// <summary>Whether the flow can function at all — a scanner AND a provisioner are wired. The
    /// OOBE wizard skips its repo step when this is false.</summary>
    public bool CanOnboard => _repoDiscovery is not null && _provisionRepo is not null;

    /// <summary>The discovered repositories, one row each with live copy state.</summary>
    public ObservableCollection<OnboardRepoRowViewModel> RepoRows { get; } = new();

    /// <summary>True while a chosen folder is being scanned for git repositories.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepoChoice))]
    private bool _isRepoScanning;

    /// <summary>An advisory line (empty scan, skipped non-repo picks, a scan error). Never blocks
    /// anything — the choice buttons and the way out stay live under it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepoNotice))]
    private string? _repoNotice;

    public bool HasRepoNotice => !string.IsNullOrEmpty(RepoNotice);

    /// <summary>True while a chosen set is copying into GitLoom OS (a mirror clone per repo —
    /// minutes for a large repository, not seconds).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopySelectedReposCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSkipRepos))]
    [NotifyPropertyChangedFor(nameof(ShowContinueRepos))]
    [NotifyPropertyChangedFor(nameof(ShowCopyReposAccent))]
    [NotifyPropertyChangedFor(nameof(ShowCopyReposPrimary))]
    [NotifyPropertyChangedFor(nameof(ShowRepoChooseAgain))]
    private bool _isProvisioningRepos;

    public bool HasRepoRows => RepoRows.Count > 0;

    /// <summary>The "how do you keep your repos" choice view — shown until a scan/pick produced rows.</summary>
    public bool IsRepoChoice => !HasRepoRows && !IsRepoScanning;

    /// <summary>At least one row completed the whole pipeline — the host's primary becomes Continue.</summary>
    public bool AnyRepoOnboarded => RepoRows.Any(r => r.IsOnboarded);

    private bool AnyRepoOnboardable => RepoRows.Any(r => !r.IsOnboarded);

    // Footer button matrix, state-derived exactly like the CLI step (no session memory): nothing
    // onboarded → Skip/Close + Copy (the view's one Accent); something onboarded → Continue/Close is
    // the Accent and Copy demotes to Primary for the remainder; copying → Cancel only.
    public bool ShowSkipRepos => !IsProvisioningRepos && !AnyRepoOnboarded;
    public bool ShowContinueRepos => !IsProvisioningRepos && AnyRepoOnboarded;
    public bool ShowCopyReposAccent => !IsProvisioningRepos && !AnyRepoOnboarded && AnyRepoOnboardable;
    public bool ShowCopyReposPrimary => !IsProvisioningRepos && AnyRepoOnboarded && AnyRepoOnboardable;

    /// <summary>Back to the choice view (wrong folder picked) — only before anything was copied.</summary>
    public bool ShowRepoChooseAgain => HasRepoRows && !IsProvisioningRepos && !AnyRepoOnboarded;

    /// <summary>Choice A — "I keep my repos in one folder": pick the folder, persist it as the
    /// sidebar's auto-detect path (the SAME preference the existing feature uses), and scan it with
    /// the existing discovery walk. An empty or failed scan leaves an advisory line, never an error
    /// state — the way out stays live throughout.</summary>
    [RelayCommand]
    private async Task PickRepoFolderAsync()
    {
        if (_pickRootFolder is null || _repoDiscovery is null)
            return;

        string? root;
        try
        {
            root = await _pickRootFolder().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RepoNotice = $"Mainguard could not open the folder picker: {ex.Message}";
            return;
        }

        if (string.IsNullOrEmpty(root))
            return; // picker dismissed — nothing changes

        RepoNotice = null;
        _settingsService?.Update(p => p.AutoDetectPath = root);
        IsRepoScanning = true;
        try
        {
            var found = await Task.Run(() => _repoDiscovery.DiscoverRepositories(root)).ConfigureAwait(true);
            SetRepoRows(found);
            if (found.Count == 0)
                RepoNotice = $"No git repositories were found in {root}. Pick a different folder, "
                    + "or point Mainguard at individual repositories.";
        }
        catch (Exception ex)
        {
            RepoNotice = $"Mainguard could not scan {root}: {ex.Message} Pick a different folder, "
                + "or point Mainguard at individual repositories.";
        }
        finally
        {
            IsRepoScanning = false;
            RaiseRepoStateChanged();
        }
    }

    /// <summary>Choice B — "Pick individual repositories": multi-folder pick, each validated with the
    /// existing git-repo check. Valid picks append (deduped by path); invalid ones are named in the
    /// advisory line rather than silently dropped.</summary>
    [RelayCommand]
    private async Task PickIndividualReposAsync()
    {
        if (_pickIndividualFolders is null || _repoDiscovery is null)
            return;

        IReadOnlyList<string> picked;
        try
        {
            picked = await _pickIndividualFolders().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            RepoNotice = $"Mainguard could not open the folder picker: {ex.Message}";
            return;
        }

        if (picked.Count == 0)
            return; // picker dismissed — nothing changes

        RepoNotice = null;
        var skipped = new List<string>();
        foreach (var path in picked)
        {
            var isRepo = await Task.Run(() => _repoDiscovery.IsGitRepository(path)).ConfigureAwait(true);
            if (!isRepo)
            {
                skipped.Add(path);
                continue;
            }

            if (!RepoRows.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
                AttachRepoRow(new OnboardRepoRowViewModel(path));
        }

        if (skipped.Count > 0)
            RepoNotice = $"Skipped {skipped.Count} folder(s) that are not git repositories: "
                + string.Join(", ", skipped.Select(System.IO.Path.GetFileName)) + ".";
        RaiseRepoStateChanged();
    }

    /// <summary>
    /// Copies the checked repositories into GitLoom OS one at a time (the daemon mirrors each host
    /// repo into the VM; sequential keeps the progress honest and the daemon uncontended), driving
    /// each row's own progress state. Failure-isolated exactly like the CLI installs: a repo that
    /// fails shows its actionable cause on its row (and stays checked, so Copy again retries it)
    /// and the rest still copy — nothing here can ever fail the host surface. A repo that is
    /// already in GitLoom OS simply succeeds again: the daemon-side provision and the sync-remote
    /// registration are both idempotent, so nothing is duplicated.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCopySelectedRepos))]
    private async Task CopySelectedReposAsync()
    {
        if (_provisionRepo is null)
            return;
        var chosen = RepoRows.Where(r => r.IsSelected && !r.IsOnboarded).ToList();
        if (chosen.Count == 0)
            return;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsProvisioningRepos = true;
        try
        {
            foreach (var row in chosen)
            {
                if (ct.IsCancellationRequested)
                    break; // later rows keep their checkbox — re-Copy or leaving both work
                row.IsFailed = false;
                row.IsProvisioning = true;
                row.StatusMessage = "Copying into Mainguard OS — a large repository can take a few minutes.";
                try
                {
                    await _provisionRepo(row.Path, ct).ConfigureAwait(true);
                    // Into the app's ONE repo store (the sidebar's) so it is there on first launch.
                    _persistRepo?.Invoke(row.Path);
                    row.IsOnboarded = true;
                    row.IsSelected = false;
                    row.StatusMessage = null;
                }
                catch (OperationCanceledException)
                {
                    row.StatusMessage = "Cancelled. Nothing else was changed — Mainguard copies this "
                        + "repository automatically the first time you open it.";
                    break;
                }
                catch (Exception ex)
                {
                    row.IsFailed = true;
                    row.StatusMessage = FriendlyRepoCopyError(ex);
                }
                finally
                {
                    row.IsProvisioning = false;
                }
            }
        }
        finally
        {
            IsProvisioningRepos = false;
        }
    }

    private bool CanCopySelectedRepos() =>
        !IsProvisioningRepos && RepoRows.Any(r => r.IsSelected && r.CanSelect);

    /// <summary>Aborts the in-flight copies (the user is never stranded watching a mirror clone).
    /// The in-flight row reports the cancellation on itself; the surface stays open for retry.</summary>
    [RelayCommand]
    private void CancelRepoCopy() => _cts?.Cancel();

    /// <summary>Back to the two-choice view (wrong folder picked). Only offered before anything was
    /// copied, so it can simply clear the list.</summary>
    [RelayCommand]
    private void ChooseReposAgain()
    {
        SetRepoRows(Array.Empty<string>());
        RepoNotice = null;
        RaiseRepoStateChanged();
    }

    private void SetRepoRows(IReadOnlyList<string> paths)
    {
        foreach (var row in RepoRows)
            row.PropertyChanged -= OnRepoRowChanged;
        RepoRows.Clear();
        foreach (var path in paths)
            AttachRepoRow(new OnboardRepoRowViewModel(path));
    }

    private void AttachRepoRow(OnboardRepoRowViewModel row)
    {
        row.PropertyChanged += OnRepoRowChanged;
        RepoRows.Add(row);
    }

    private void OnRepoRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OnboardRepoRowViewModel.IsSelected) or nameof(OnboardRepoRowViewModel.IsOnboarded))
            RaiseRepoStateChanged();
    }

    private void RaiseRepoStateChanged()
    {
        OnPropertyChanged(nameof(HasRepoRows));
        OnPropertyChanged(nameof(IsRepoChoice));
        OnPropertyChanged(nameof(AnyRepoOnboarded));
        OnPropertyChanged(nameof(ShowSkipRepos));
        OnPropertyChanged(nameof(ShowContinueRepos));
        OnPropertyChanged(nameof(ShowCopyReposAccent));
        OnPropertyChanged(nameof(ShowCopyReposPrimary));
        OnPropertyChanged(nameof(ShowRepoChooseAgain));
        CopySelectedReposCommand.NotifyCanExecuteChanged();
    }

    private static string FriendlyRepoCopyError(Exception ex)
    {
        // The one failure everyone hits eventually: the GitLoom OS daemon is not reachable (VM idle,
        // still booting, or setup incomplete). Name IT, not a gRPC status code — and the way forward.
        if (ex is Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Unavailable })
            return "Mainguard OS could not be reached — it may still be starting up. Wait a moment and "
                + "copy again, or skip: Mainguard copies this repository automatically the first time you open it.";
        var reason = ex is Grpc.Core.RpcException rpc
            ? (rpc.Status.Detail is { Length: > 0 } detail ? detail : $"the Mainguard OS daemon reported {rpc.StatusCode}")
            : ex.Message;
        return $"This repository was not copied: {reason} The others continue — you can retry it here, "
            + "or skip: Mainguard copies it automatically the first time you open it.";
    }
}
