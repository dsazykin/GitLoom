using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mainguard.Agents.Agents.Bootstrap;
using Mainguard.Agents.Daemon;

namespace GitLoom.App.ViewModels;

/// <summary>
/// The read-only "Daemon logs" settings surface: pick a source — the unified journal (all subsystems
/// interleaved) or one per-subsystem file — and read its recent lines, with a Copy button. A thin
/// projection over Core's <see cref="DaemonLogReader"/> (the journald/tail-over-WSL seam); it never
/// writes anything and, like the reader, never throws — an unreachable VM renders an honest empty state
/// rather than an error dialog.
///
/// <para>Constructed directly (no DI, per the App's static-<c>Settings</c> pattern); the reader is an
/// injectable seam so the render harness can supply fixed text. The clipboard write is a settable
/// <see cref="CopyAction"/> the View wires, keeping the ViewModel display-free.</para>
/// </summary>
public partial class DaemonLogsViewModel : ViewModelBase, IDisposable
{
    /// <summary>The dropdown label for the unified journal source (vs a single subsystem file).</summary>
    public const string JournalLabel = "All subsystems (journal)";

    private const int LineCount = 400;

    private readonly DaemonLogReader? _reader;
    private CancellationTokenSource? _cts;

    /// <summary>Live constructor: reads the real in-VM logs over WSL.</summary>
    public DaemonLogsViewModel(DaemonLogReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        BuildSources();
        _selectedSource = JournalLabel;
    }

    /// <summary>Design/render constructor: fixed representative text + state, no reader behind it.</summary>
    public DaemonLogsViewModel(string? logText, bool isLoading = false, string? selectedSource = null)
    {
        BuildSources();
        _selectedSource = selectedSource ?? JournalLabel;
        _logText = logText;
        _isLoading = isLoading;
    }

    /// <summary>The source picker: the journal, then each per-subsystem file (canonical order).</summary>
    public ObservableCollection<string> Sources { get; } = new();

    [ObservableProperty]
    private string _selectedSource;

    /// <summary>The recent lines of the selected source. Null/empty renders the honest empty state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLogText))]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string? _logText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isLoading;

    public bool HasLogText => !string.IsNullOrWhiteSpace(LogText);

    /// <summary>Loaded, but the source had nothing (VM down / no such log yet) — an honest empty line.</summary>
    public bool ShowEmpty => !IsLoading && !HasLogText;

    /// <summary>Wired from the View so Close works from the ViewModel (the AgentCliSettings pattern).</summary>
    public Action? CloseAction { get; set; }

    /// <summary>Wired from the View to write to the OS clipboard, keeping the ViewModel display-free.</summary>
    public Action<string>? CopyAction { get; set; }

    private void BuildSources()
    {
        Sources.Add(JournalLabel);
        foreach (var subsystem in DaemonLogSubsystems.All)
            Sources.Add(subsystem);
    }

    partial void OnSelectedSourceChanged(string value)
    {
        // Switching source re-reads it (no-op for the design/render instance — no reader behind it).
        if (_reader is not null && RefreshCommand.CanExecute(null))
            RefreshCommand.Execute(null);
    }

    /// <summary>Reads the selected source's recent lines. Never faults — an unreachable VM yields "".</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (_reader is null)
            return; // design/render instance

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsLoading = true;
        try
        {
            var text = string.Equals(SelectedSource, JournalLabel, StringComparison.Ordinal)
                ? await _reader.ReadRecentAsync(LineCount, ct).ConfigureAwait(true)
                : await _reader.ReadSubsystemAsync(SelectedSource, LineCount, ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested)
                LogText = text;
        }
        catch (OperationCanceledException)
        {
            // A newer refresh superseded this one — leave its result to win.
        }
        finally
        {
            if (!ct.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private bool CanRefresh() => !IsLoading;

    [RelayCommand(CanExecute = nameof(HasLogText))]
    private void Copy()
    {
        if (LogText is { Length: > 0 } text)
            CopyAction?.Invoke(text);
    }

    [RelayCommand]
    private void Close() => CloseAction?.Invoke();

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        GC.SuppressFinalize(this);
    }
}
