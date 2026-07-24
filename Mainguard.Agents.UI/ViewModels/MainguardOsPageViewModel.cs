using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The Settings "Mainguard OS" page: the repo-onboarding engine (<see cref="AddRepos"/>, the same
/// content the old standalone "Add Repos to Mainguard OS" window hosted) plus the "Rebuild sandbox
/// images" action, combined because Rebuild has no dialog of its own and doesn't deserve its own
/// sidebar row.
///
/// <para>As a Settings page, the old Window.Closing guard ("cancel an in-flight repo copy rather
/// than leave it orphaned") becomes <see cref="OnDeactivated"/> — the page can be navigated away
/// from mid-copy exactly as the window used to be closed mid-copy, and must not leave that copy
/// running unattended either way.</para>
/// </summary>
public sealed partial class MainguardOsPageViewModel : ViewModelBase, ISettingsPage
{
    private readonly Func<Task> _rebuildSandboxImages;

    public MainguardOsPageViewModel(AddReposToOsViewModel addRepos, Func<Task> rebuildSandboxImages)
    {
        AddRepos = addRepos;
        _rebuildSandboxImages = rebuildSandboxImages;
    }

    public AddReposToOsViewModel AddRepos { get; }

    [RelayCommand]
    private Task RebuildSandboxImages() => _rebuildSandboxImages();

    public void OnActivated()
    {
    }

    public void OnDeactivated()
    {
        if (AddRepos.IsProvisioningRepos)
            AddRepos.CancelRepoCopyCommand.Execute(null);
    }
}
