using System;
using Mainguard.Agents.Terminal;

namespace Mainguard.Agents.UI.Controls;

/// <summary>
/// What <c>TerminalView</c>'s code-behind needs from a concrete engine control: the
/// <see cref="ITerminalView"/> seam the ViewModel consumes (unchanged by P2-18 — that is the whole
/// point) plus the layout-resize event the view routes back to the ViewModel. Implemented by both
/// the interim <see cref="TerminalControl"/> and the P2-18 <see cref="TerminalGridControl"/> so the
/// view can host either behind the <c>TerminalEngine</c> flag without knowing which it got.
/// </summary>
internal interface ITerminalEngineControl : ITerminalView
{
    /// <summary>Raised when the control's own layout produces a new (cols, rows) size.</summary>
    event EventHandler<TerminalResizeEventArgs>? UserResized;
}
