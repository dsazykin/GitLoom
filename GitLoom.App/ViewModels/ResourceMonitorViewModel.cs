using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitLoom.Core.Agents;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The task-manager-style resource monitor (revised design 2026-07-11): totals up top,
/// one live row per agent (CPU / RAM / spend / state / task), right-click Pause/Resume and
/// End task (End confirms first — it rejects the work and tears the sandbox down; the branch
/// is kept until teardown, V-5). Readouts tick Still; no accent — telemetry earns silence.
/// </summary>
public partial class ResourceMonitorViewModel : ViewModelBase, IDisposable
{
    private readonly IAgentService _agents;
    private readonly ITelemetryService _telemetry;

    public ObservableCollection<AgentUsageRowViewModel> Rows { get; } = new();

    [ObservableProperty] private string _totalsText = "";
    [ObservableProperty] private Points _cpuPoints = new();

    // End-task confirmation (C-1/C-2: the object named, the recoverable stated).
    [ObservableProperty] private bool _isEndConfirmVisible;
    [ObservableProperty] private string _endConfirmTitle = "";
    [ObservableProperty] private string _endConfirmMessage = "";
    private string? _pendingEndAgentId;

    public ResourceMonitorViewModel(IAgentService agents, ITelemetryService telemetry)
    {
        _agents = agents;
        _telemetry = telemetry;
        _telemetry.Sampled += OnSampled;
        Refresh();
    }

    private void OnSampled() => Dispatcher.UIThread.Post(Refresh);

    private void Refresh()
    {
        var usage = _telemetry.GetAgentUsage();

        for (int i = Rows.Count - 1; i >= 0; i--)
            if (usage.All(u => u.AgentId != Rows[i].AgentId))
                Rows.RemoveAt(i);
        // Stable order (insertion order, new agents append): reordering live rows would
        // yank an open context menu shut and make targets jump under the cursor.
        foreach (var u in usage)
        {
            var row = Rows.FirstOrDefault(r => r.AgentId == u.AgentId);
            if (row is null) Rows.Add(row = new AgentUsageRowViewModel(u.AgentId, this));
            row.Update(u);
        }

        var total = _telemetry.Current;
        TotalsText = $"CPU {total.CpuPercent:0}%   ·   RAM {total.RamGb:0.0} GB   ·   spend today ${total.SpendTodayUsd:0.00}   ·   {Rows.Count} agents";

        var history = _telemetry.History;
        var points = new Points();
        int n = Math.Min(60, history.Count);
        for (int i = 0; i < n; i++)
        {
            var s = history[history.Count - n + i];
            points.Add(new Point(i * (240.0 / Math.Max(1, n - 1)), 20 - s.CpuPercent / 100.0 * 20));
        }
        CpuPoints = points;
    }

    // ---- row actions (invoked from the context menu) ----

    public async Task PauseOrResumeAsync(string agentId, bool isPaused)
    {
        if (isPaused) await _agents.ResumeAgentAsync(agentId);
        else await _agents.PauseAgentAsync(agentId);
        Refresh();
    }

    public void RequestEnd(string agentId, string name)
    {
        _pendingEndAgentId = agentId;
        EndConfirmTitle = $"End {name}?";
        EndConfirmMessage = $"{name}'s work is rejected and its sandbox is torn down. " +
                            "Its branch is kept until teardown, so nothing is silently lost.";
        IsEndConfirmVisible = true;
    }

    [RelayCommand]
    private async Task ConfirmEndAsync()
    {
        IsEndConfirmVisible = false;
        if (_pendingEndAgentId is { } id)
        {
            _pendingEndAgentId = null;
            await _agents.EndAgentAsync(id);
            Refresh();
        }
    }

    [RelayCommand]
    private void CancelEnd()
    {
        IsEndConfirmVisible = false;
        _pendingEndAgentId = null;
    }

    public void Dispose() => _telemetry.Sampled -= OnSampled;
}

/// <summary>One agent's live usage row; the context menu drives pause/resume/end.</summary>
public partial class AgentUsageRowViewModel : ViewModelBase
{
    private readonly ResourceMonitorViewModel _owner;

    public string AgentId { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _stateWord = "";
    [ObservableProperty] private string _cpuText = "";
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _spendText = "";
    [ObservableProperty] private string _task = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _pauseMenuLabel = "Pause";

    public AgentUsageRowViewModel(string agentId, ResourceMonitorViewModel owner)
    {
        AgentId = agentId;
        _owner = owner;
    }

    public void Update(AgentResourceUsage usage)
    {
        Name = usage.Name;
        StateWord = usage.StateWord;
        CpuText = $"{usage.CpuPercent:0}%";
        RamText = $"{usage.RamGb:0.0} GB";
        SpendText = $"${usage.SpendUsd:0.00}";
        Task = usage.Task;
        IsPaused = usage.IsPaused;
        PauseMenuLabel = usage.IsPaused ? "Resume" : "Pause";
    }

    [RelayCommand]
    private System.Threading.Tasks.Task PauseOrResumeAsync() => _owner.PauseOrResumeAsync(AgentId, IsPaused);

    [RelayCommand]
    private void EndTask() => _owner.RequestEnd(AgentId, Name);
}
