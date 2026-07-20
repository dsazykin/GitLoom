using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Services;
using Mainguard.Git.Services;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The post-setup "Add Repos to GitLoom OS" window (Tools menu) — the add-more-later surface for
/// repositories, the sibling of <see cref="AgentCliSettingsViewModel"/>: a user who skipped the
/// OOBE repo-onboarding step (or whose copies failed there) gets the SAME flow after setup, so
/// their repositories are agent-ready without opening each one once. It IS the OOBE step's engine
/// (<see cref="RepoOnboardingViewModel"/>) with a window Close on top — the two surfaces share one
/// implementation and cannot drift: same discovery walk, same per-repo provision pipeline
/// (idempotent — re-adding an already-copied repo just succeeds, nothing duplicates), same per-row
/// failure isolation and cancellation, and the same "GitLoom copies it automatically the first time
/// you open it" story when the user bails out.
///
/// <para>Constructed directly (no DI) by <c>App.CreateAddReposToOsViewModel</c>, which wires the
/// live seams; the seam-shaped constructor is what the tests drive with fakes.</para>
/// </summary>
public partial class AddReposToOsViewModel : RepoOnboardingViewModel
{
    /// <summary>Live constructor: the real discovery walk, this window's folder pickers, and the
    /// P2-06 provision+register pipeline.</summary>
    public AddReposToOsViewModel(
        IRepoDiscoveryService repoDiscovery,
        Func<Task<string?>> pickRootFolder,
        Func<Task<IReadOnlyList<string>>> pickIndividualFolders,
        Func<string, CancellationToken, Task> provisionRepo,
        Action<string>? persistRepo = null,
        ISettingsService? settingsService = null)
        : base(repoDiscovery, pickRootFolder, pickIndividualFolders, provisionRepo, persistRepo, settingsService)
    {
    }

    /// <summary>Design/render constructor: fixed representative rows, no live seams.</summary>
    public AddReposToOsViewModel(
        IEnumerable<OnboardRepoRowViewModel>? rows = null,
        bool isProvisioning = false,
        bool isScanning = false,
        string? notice = null)
        : base(rows, isProvisioning, isScanning, notice)
    {
    }

    /// <summary>Wired from the View so Close works from the ViewModel (the AgentCliSettings pattern).</summary>
    public Action? CloseAction { get; set; }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();
}
