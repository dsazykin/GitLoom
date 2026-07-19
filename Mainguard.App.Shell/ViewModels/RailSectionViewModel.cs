using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.App.Shell.Editions;
using Mainguard.UI.Editions;
using Mainguard.UI.ViewModels;

namespace Mainguard.App.Shell.ViewModels;

/// <summary>
/// One data-driven section-rail destination (1b), materialized by the shell from a
/// <see cref="RailSectionDescriptor"/> off <c>App.Edition.Sections</c>. The rail's item template in
/// <c>MainWindow.axaml</c> binds against this VM; activation routes back to the shell by
/// <see cref="Id"/> so each section keeps its exact per-section behavior (Resources lazily builds its
/// monitor, Coordinator focuses the conversation, host tabs run their async open).
///
/// Green-keeping: the descriptor is the 1a contract and stays untouched, so the shipped tooltips
/// (which are richer than the bare label for Repo/Coordinator/Resources) are reproduced here by
/// <see cref="Id"/> rather than by widening the descriptor — behavior under Pro is byte-identical.
/// </summary>
public partial class RailSectionViewModel : ViewModelBase
{
    private readonly Action<string> _activate;

    public RailSectionViewModel(RailSectionDescriptor descriptor, Action<string> activate, bool showsLeadingDivider)
    {
        _activate = activate;
        Id = descriptor.Id;
        Label = descriptor.Label;
        IconResourceKey = descriptor.IconResourceKey;
        RequiresWorkspace = descriptor.RequiresWorkspace;
        Adornment = descriptor.Adornment;
        ShowsLeadingDivider = showsLeadingDivider;
        // Host tabs (RequiresWorkspace) start disabled until a repo is open — exactly as the shipped
        // rail's per-button `IsEnabled="{Binding CurrentWorkspace, ...IsNotNull}"`; the shell flips this
        // when CurrentWorkspace changes. Every other destination is always enabled.
        _isEnabled = !descriptor.RequiresWorkspace;
    }

    /// <summary>Stable section id (matches <c>ActivateSection</c>'s switch: Repo / Coordinator /
    /// Resources / PullRequests / Issues / Notifications / Releases).</summary>
    public string Id { get; }

    /// <summary>The rail label (shown when the rail is expanded).</summary>
    public string Label { get; }

    /// <summary>The icon resource key, resolved in XAML via <c>ResourceKeyToGeometryConverter</c>.</summary>
    public string IconResourceKey { get; }

    /// <summary>True when this destination needs an open workspace (the four host tabs).</summary>
    public bool RequiresWorkspace { get; }

    /// <summary>This section's adornment (attention badge / spend readout / none).</summary>
    public RailAdornmentKind Adornment { get; }

    /// <summary>True for the Coordinator row — drives the attention dot + count badge (the live values
    /// come from the shell's control center, bound through the parent window in the item template).</summary>
    public bool ShowsAttention => Adornment == RailAdornmentKind.Attention;

    /// <summary>True for the Resources row — drives the inline spend readout (live value bound through
    /// the parent window).</summary>
    public bool ShowsSpend => Adornment == RailAdornmentKind.Spend;

    /// <summary>True on the first workspace-scoped destination, so the item template draws the shipped
    /// hairline that separates the git/agent sections from the host tabs.</summary>
    public bool ShowsLeadingDivider { get; }

    /// <summary>The rail tooltip — richer than the label for the three platform destinations, exactly as
    /// the shipped hard-coded rail; host tabs fall back to the label (their shipped tooltip == label).</summary>
    public string Tooltip => Id switch
    {
        "Repo" => "Repo viewer — the git workspace",
        "Coordinator" => "Coordinator — plan approvals, chat, and the merge queue",
        "Resources" => "Resources — sandbox CPU, RAM, and token spend",
        _ => Label,
    };

    /// <summary>Whether this is the active section — set by the shell (SelectedSectionId == Id); drives
    /// the button's <c>active</c> class.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>Whether the row is clickable — always true except the host tabs, which follow the open
    /// workspace (the shell flips this on CurrentWorkspace changes).</summary>
    [ObservableProperty] private bool _isEnabled;

    /// <summary>Activate this section — routed back to the shell by id so the exact per-section behavior
    /// (lazy Resources monitor, Coordinator focus, async host-tab open) is preserved.</summary>
    [RelayCommand]
    private void Activate() => _activate(Id);
}
