using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// The small shutdown window (owner design, 2026-07-17): a changing status line for the teardown
/// that already happens — releasing the VM keep-alive, and (when StopVmOnExit is on) stopping
/// GitLoom OS — visualized so a full exit isn't a silent freeze while <c>wsl --terminate</c> runs.
/// Implements <see cref="IProgress{String}"/> so <see cref="AppShutdownSequence"/> reports straight
/// onto <see cref="StatusText"/> (marshalled to the UI thread).
/// </summary>
public sealed partial class ShutdownWindowViewModel : ViewModelBase, IProgress<string>
{
    /// <summary>Live constructor.</summary>
    public ShutdownWindowViewModel()
    {
    }

    /// <summary>Design/render constructor: fixed status line (e.g. mid-teardown "Stopping GitLoom OS…").</summary>
    public ShutdownWindowViewModel(string statusText)
    {
        _statusText = statusText;
    }

    /// <summary>The one-line changing status text.</summary>
    [ObservableProperty]
    private string _statusText = ShutdownStatus.ReleasingKeepAlive;

    /// <summary>Shutdown-sequence progress sink: marshals each status line to the UI thread.</summary>
    public void Report(string value) => Dispatcher.UIThread.Post(() => StatusText = value);
}
