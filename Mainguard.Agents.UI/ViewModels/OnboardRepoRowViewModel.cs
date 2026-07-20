using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// One discovered host repository in the OOBE repo-onboarding step (the sibling of
/// <see cref="AgentCliRowViewModel"/>): name + path, a checkbox while there is something to decide,
/// then the copy-into-Mainguard-OS lifecycle per row. State is encoded for the view as booleans so
/// status always renders as icon AND text, never colour alone.
/// </summary>
public partial class OnboardRepoRowViewModel : ViewModelBase
{
    public OnboardRepoRowViewModel(string path, bool isOnboarded = false)
    {
        Path = path;
        // These are Windows HOST paths, but the tests also run on the Linux CI leg where '\' is not
        // a directory separator — so split on both explicitly instead of Path.GetFileName.
        var trimmed = path.TrimEnd('/', '\\');
        var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
        Name = string.IsNullOrEmpty(name) ? path : name;
        _isOnboarded = isOnboarded;
        // Default checked — the user pointed Mainguard at these on purpose; unchecking is the opt-out.
        _isSelected = !isOnboarded;
    }

    /// <summary>The host (Windows-side) working-tree path — what the daemon provisions from.</summary>
    public string Path { get; }

    /// <summary>The repository's folder name (the row title).</summary>
    public string Name { get; }

    /// <summary>The onboarding checkbox state (default checked for a fresh discovery).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private bool _isSelected;

    /// <summary>True once the repo was provisioned into Mainguard OS AND registered in the app's repo
    /// list — "onboarded" always means the whole per-repo pipeline succeeded, never just one RPC.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isOnboarded;

    /// <summary>True while this row's copy is in flight (the daemon mirrors the repo into the VM).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isProvisioning;

    /// <summary>The last copy attempt failed; <see cref="StatusMessage"/> carries the actionable cause.</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>Progress or failure detail under the row. Null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>The checkbox is live only while there is something to decide.</summary>
    public bool CanSelect => !IsOnboarded && !IsProvisioning;

    /// <summary>No lifecycle activity at all (pending dot in the list).</summary>
    public bool IsIdle => !IsOnboarded && !IsProvisioning;
}
