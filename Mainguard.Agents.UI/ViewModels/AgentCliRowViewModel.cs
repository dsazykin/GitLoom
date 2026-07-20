using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Agents.Adapters;

namespace GitLoom.App.ViewModels;

/// <summary>
/// One agent CLI as presented to the user — shared by the OOBE "choose your agents" step (checkbox
/// multi-select) and the Agent CLIs settings window (per-row Install). Projects an
/// <see cref="AgentCliOption"/> from the pinned channel plus its live install lifecycle. State is
/// encoded for the view as booleans so status always renders as icon AND text, never colour alone.
/// </summary>
public partial class AgentCliRowViewModel : ViewModelBase
{
    public AgentCliRowViewModel(AgentCliOption option)
        : this(option.Id, option.DisplayName, option.Version, option.IsInstalled)
    {
    }

    /// <summary>Design/harness constructor: fixed representative state, no service behind it.</summary>
    public AgentCliRowViewModel(string id, string displayName, string version, bool isInstalled = false)
    {
        Id = id;
        DisplayName = displayName;
        Version = version;
        _isInstalled = isInstalled;
    }

    /// <summary>The channel adapter id (== the daemon's <c>agentKind</c>, e.g. <c>claude-code</c>).</summary>
    public string Id { get; }

    public string DisplayName { get; }

    /// <summary>The pinned version the channel installs — concrete by construction (never @latest).</summary>
    public string Version { get; }

    /// <summary>The version chip text (<c>v2.1.210</c>).</summary>
    public string VersionLabel => $"v{Version}";

    /// <summary>OOBE picker checkbox state. Unused by the settings window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private bool _isSelected;

    /// <summary>True once the version-matched health probe reports the pinned version — "installed"
    /// here always means "installed AND runnable AND the pinned version", never just "npm exited 0".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool _isInstalling;

    /// <summary>The last install attempt failed; <see cref="StatusMessage"/> carries the actionable
    /// cause (from the typed channel refusal — hash mismatch, in-VM install failure, probe failure).</summary>
    [ObservableProperty]
    private bool _isFailed;

    /// <summary>Progress or failure detail under the row. Null when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    private string? _statusMessage;

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    /// <summary>The OOBE checkbox is live only while there is something to decide.</summary>
    public bool CanSelect => !IsInstalled && !IsInstalling;

    /// <summary>Not installed, not installing — the settings row offers Install in this state.</summary>
    public bool CanInstall => !IsInstalled && !IsInstalling;

    /// <summary>No lifecycle activity at all (pending dot in the OOBE list).</summary>
    public bool IsIdle => !IsInstalled && !IsInstalling;
}
