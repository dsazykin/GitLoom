using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Agents;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Sandbox health & egress panel (P2-44 / ControlCenterDesign.md §8.1): a chronological fact
/// table, read-only, no accent — telemetry earns silence. Alerts are events, never auto-kills.
/// </summary>
public partial class TelemetryPanelViewModel : ViewModelBase
{
    private readonly ITelemetryService _telemetry;

    public ObservableCollection<SandboxEventRowViewModel> Events { get; } = new();

    [ObservableProperty] private string _summaryText = "";
    [ObservableProperty] private bool _isAllClear;

    public TelemetryPanelViewModel(ITelemetryService telemetry)
    {
        _telemetry = telemetry;
        Refresh();
    }

    public void Refresh()
    {
        var events = _telemetry.GetSandboxEvents();
        // Newest-first; sync the head.
        Events.Clear();
        foreach (var e in events) Events.Add(new SandboxEventRowViewModel(e));

        IsAllClear = Events.Count == 0;
        var blocked = events.Count(e => e.Kind == "egress_denied");
        SummaryText = IsAllClear
            ? "No blocked egress, no secret access attempts — sandboxes healthy."
            : $"{events.Count} events · {blocked} blocked egress";
    }
}

public sealed class SandboxEventRowViewModel
{
    public string TimeText { get; }
    public string Agent { get; }
    public string KindWord { get; }
    public string Detail { get; }
    public string Process { get; }
    public bool IsWarning { get; }

    public SandboxEventRowViewModel(SandboxEvent e)
    {
        TimeText = e.At.ToLocalTime().ToString("HH:mm");
        Agent = e.AgentId;
        KindWord = e.Kind switch
        {
            "egress_denied" => "egress blocked",
            "quarantine_push" => "quarantine push",
            "secret_access" => "secret access attempt",
            var k => k,
        };
        Detail = e.Detail;
        Process = e.Process;
        IsWarning = e.Kind is "egress_denied" or "secret_access";
    }
}
