using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The Pro agent rail as its own surface (step 2d): the live worker list + the kill switch,
/// extracted out of <c>MainWindow.axaml</c> so the shell hosts it as opaque <c>object</c> content
/// through <see cref="ViewLocator"/> (→ <c>AgentRailView</c>) instead of naming
/// <see cref="AgentRowViewModel"/>/the kill-switch members directly. Reached from the shell only via
/// <c>MainWindowViewModel.AgentRailContent</c> → <see cref="Editions.IAgentPlatformSurface.AgentRailContent"/>,
/// both typed <c>object?</c>.
///
/// A thin view over <see cref="ControlCenterViewModel"/> — the single owner of the agent projection and
/// the kill-switch state (the coordinator surface's freeze banner binds the same <c>IsFrozen</c>). Keeping
/// the state there means the rail and the coordinator surface can never drift, and this step moves the
/// MARKUP (not the orchestration logic) out of the shell. <see cref="Agents"/> is the owner's live
/// collection (stable reference — its own notifications flow through); the two derived kill-switch
/// readouts are re-raised when the control center changes them.
/// </summary>
public sealed partial class AgentRailViewModel : ViewModelBase
{
    private readonly ControlCenterViewModel _controlCenter;

    public AgentRailViewModel(ControlCenterViewModel controlCenter)
    {
        _controlCenter = controlCenter;
        _controlCenter.PropertyChanged += OnControlCenterPropertyChanged;
    }

    /// <summary>The live worker agents shown in the rail's list (LIFO), owned by the control center.</summary>
    public ObservableCollection<AgentRowViewModel> Agents => _controlCenter.Agents;

    /// <summary>Kill-switch frozen state (drives the rail button's <c>frozen</c> class).</summary>
    public bool IsFrozen => _controlCenter.IsFrozen;

    /// <summary>The kill-switch button label ("Stop all" / "Frozen — resume").</summary>
    public string KillSwitchLabel => _controlCenter.KillSwitchLabel;

    /// <summary>The kill-switch toggle command.</summary>
    public IAsyncRelayCommand ToggleKillSwitchCommand => _controlCenter.ToggleKillSwitchCommand;

    // Re-raise the two DERIVED kill-switch readouts when the control center flips them, so the rail
    // button's frozen class + label track live. Agents needs no forwarding — it is an
    // ObservableCollection reached by a stable reference, so its own change notifications flow through.
    private void OnControlCenterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlCenterViewModel.IsFrozen))
            OnPropertyChanged(nameof(IsFrozen));
        else if (e.PropertyName == nameof(ControlCenterViewModel.KillSwitchLabel))
            OnPropertyChanged(nameof(KillSwitchLabel));
    }
}
