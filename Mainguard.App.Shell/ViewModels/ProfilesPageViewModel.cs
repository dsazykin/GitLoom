using System;
using System.Threading.Tasks;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// The Settings "Git Profiles" page: wraps <see cref="ProfilesViewModel"/> (unchanged) and replaces
/// the old <c>RepoDashboardViewModel.ManageProfilesAsync</c>'s post-<c>ShowDialog</c>
/// <c>RefreshCoreAsync()</c> call with <see cref="OnDeactivated"/> — applying a profile changes the
/// open repo's local git identity, so the dashboard needs to refresh whenever the user navigates away
/// from this page, not just when a dialog closed.
/// </summary>
public sealed class ProfilesPageViewModel : ViewModelBase, ISettingsPage
{
    private readonly Func<Task>? _onDeactivatedRefresh;

    public ProfilesPageViewModel(ProfilesViewModel inner, Func<Task>? onDeactivatedRefresh)
    {
        Inner = inner;
        _onDeactivatedRefresh = onDeactivatedRefresh;
    }

    public ProfilesViewModel Inner { get; }

    public void OnActivated()
    {
    }

    public void OnDeactivated() => _ = _onDeactivatedRefresh?.Invoke();
}
