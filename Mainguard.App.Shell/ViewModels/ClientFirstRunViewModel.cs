using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.App.Services;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The Client edition's dedicated "Clone" first-run (1d, ADR-0001). A light welcome framing around the
/// REUSED Clone Dashboard (<see cref="CloneDashboardViewModel"/> / <c>CloneDashboardView</c>): the user
/// clones a remote repo, opens a local folder, or signs into a host — and the moment a repository is
/// ready (or the user skips) the app swaps to the shell. This VM is shell-side and constructs NONE of the
/// Pro GitLoomOS/daemon/bootstrap types: it holds the reused clone VM, the "get your first repository"
/// commands, and raises <see cref="Completed"/> for the window's code-behind to swap to <c>MainWindow</c>.
///
/// The interactions that need a window owner (destination picker, device-flow dialog, local-folder open,
/// Accounts/SSH windows) are wired by <c>App.CreateClientFirstRunWindow</c> — the SAME shape as
/// <c>MainWindowViewModel.OpenCloudSync</c> — through the request hooks below, so this VM stays UI-free and
/// unit-constructible.
/// </summary>
public partial class ClientFirstRunViewModel : ViewModelBase
{
    /// <summary>The REUSED Clone Dashboard — remote clone + inline GitHub device-flow sign-in, unchanged.</summary>
    public CloneDashboardViewModel Clone { get; }

    /// <summary>Raised when a repository is cloned/opened OR the user skips — the window swaps to the shell.</summary>
    public event EventHandler? Completed;

    /// <summary>Opens a local-folder picker (validates + registers the repo). Set by App where the window exists.</summary>
    public Func<Task>? OpenLocalFolderRequested { get; set; }

    /// <summary>Opens the shared multi-host Accounts sign-in window. Set by App where the window exists.</summary>
    public Action? ManageAccountsRequested { get; set; }

    /// <summary>Opens the shared SSH-keys window. Set by App where the window exists.</summary>
    public Action? ManageSshKeysRequested { get; set; }

    /// <summary>An advisory message when "Open a local folder" pointed at a non-Git directory.</summary>
    [ObservableProperty]
    private string? _localFolderError;

    public ClientFirstRunViewModel(CloneDashboardViewModel clone)
    {
        Clone = clone ?? throw new ArgumentNullException(nameof(clone));
    }

    /// <summary>Registers the cloned/opened repository into the ONE sidebar store (so it is there when the
    /// shell opens) and proceeds to the shell. Called from the clone-complete + local-folder-open wiring.</summary>
    public void CompleteWithRepo(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            RepoCatalog.EnsureRegistered(path);
        Completed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"Skip for now" — proceed to the shell with no repository; its empty-state
    /// ("Select a repository to begin") covers the rest.</summary>
    [RelayCommand]
    private void Skip() => Completed?.Invoke(this, EventArgs.Empty);

    /// <summary>"Open a local folder" — delegates to the App-supplied picker (which validates + registers).</summary>
    [RelayCommand]
    private async Task OpenLocalFolder()
    {
        LocalFolderError = null;
        if (OpenLocalFolderRequested is { } request)
            await request();
    }

    /// <summary>"Sign in to a host" — opens the shared Accounts window.</summary>
    [RelayCommand]
    private void ManageAccounts() => ManageAccountsRequested?.Invoke();

    /// <summary>"SSH keys" — opens the shared SSH-keys window.</summary>
    [RelayCommand]
    private void ManageSshKeys() => ManageSshKeysRequested?.Invoke();
}
