using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Mainguard.Agents.Terminal;
using Mainguard.Agents.UI.Services;
using Mainguard.UI.ViewModels;

namespace Mainguard.Agents.UI.ViewModels;

/// <summary>
/// Drives one attached terminal: it wires the engine (behind <see cref="ITerminalView"/>) to the
/// daemon stream (behind <see cref="ITerminalGateway"/>) and owns nothing about VT parsing or
/// rendering — that all lives in the engine. Because the VM only ever touches the
/// <see cref="ITerminalView"/> interface, P2-18 swaps the engine with no VM change (invariant 3).
///
/// <para>Keystrokes surfaced by the engine (<see cref="ITerminalView.InputAvailable"/>, incl.
/// Ctrl+C → 0x03) are forwarded to the daemon; daemon output is fed back into the engine; and
/// layout-driven resizes are debounced (~50 ms) before both resizing the engine and notifying the
/// daemon (SIGWINCH).</para>
/// </summary>
public sealed partial class TerminalViewModel : ViewModelBase, IDisposable
{
    private readonly ITerminalGateway _gateway;
    private readonly TimeSpan _resizeDebounce;

    private ITerminalView? _view;
    private CancellationTokenSource? _resizeCts;
    private int _pendingCols;
    private int _pendingRows;
    private bool _disposed;

    [ObservableProperty]
    private string _agentId = string.Empty;

    /// <summary>True once the PTY has streamed its first output frame — the surface's "the CLI is
    /// actually drawing" signal, used to replace a startup loading animation with the live terminal.</summary>
    [ObservableProperty]
    private bool _hasReceivedOutput;

    public TerminalViewModel(ITerminalGateway gateway, TimeSpan? resizeDebounce = null)
    {
        _gateway = gateway;
        _resizeDebounce = resizeDebounce ?? TimeSpan.FromMilliseconds(50);
        _gateway.OutputReceived += OnOutputReceived;
    }

    /// <summary>Binds the concrete engine control (the View supplies it — the VM keeps only the interface).</summary>
    public void AttachView(ITerminalView view)
    {
        if (_view is not null)
        {
            _view.InputAvailable -= OnInputAvailable;
        }

        _view = view;
        _view.InputAvailable += OnInputAvailable;
    }

    /// <summary>Opens the daemon attach stream for <paramref name="agentId"/>.</summary>
    public Task AttachAsync(string agentId, CancellationToken ct)
    {
        AgentId = agentId;
        return _gateway.AttachAsync(agentId, ct);
    }

    /// <summary>Called by the engine control when its layout resolves a new (cols, rows) size.</summary>
    public void OnUserResize(int cols, int rows)
    {
        _pendingCols = cols;
        _pendingRows = rows;

        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        var cts = new CancellationTokenSource();
        _resizeCts = cts;
        _ = DebounceResizeAsync(cts.Token);
    }

    private async Task DebounceResizeAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_resizeDebounce, ct);
        }
        catch (OperationCanceledException)
        {
            return; // a newer resize superseded this one
        }

        _view?.Resize(_pendingCols, _pendingRows);
        try
        {
            await _gateway.SendResizeAsync(_pendingCols, _pendingRows);
        }
        catch (OperationCanceledException)
        {
            // Stream torn down.
        }
    }

    private void OnInputAvailable(byte[] data) => _ = SendInputAsync(data);

    private async Task SendInputAsync(byte[] data)
    {
        try
        {
            await _gateway.SendInputAsync(data);
        }
        catch (OperationCanceledException)
        {
            // Stream torn down.
        }
    }

    private void OnOutputReceived(ReadOnlyMemory<byte> data)
    {
        if (!HasReceivedOutput && data.Length > 0)
        {
            HasReceivedOutput = true;
        }

        _view?.FeedOutput(data);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _resizeCts?.Cancel();
        _resizeCts?.Dispose();
        _gateway.OutputReceived -= OnOutputReceived;
        if (_view is not null)
        {
            _view.InputAvailable -= OnInputAvailable;
        }
    }
}
