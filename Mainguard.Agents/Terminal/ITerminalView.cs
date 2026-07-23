using System;

namespace Mainguard.Agents.Terminal;

/// <summary>
/// The engine-agnostic seam between the terminal ViewModel and whatever renders/parses VT bytes.
/// The interim engine (a vendored/minimal Avalonia cell-grid renderer) sits behind this today;
/// P2-18 swaps a server-side libvterm grid engine in behind the same interface <b>without any
/// ViewModel change</b> — which is the entire point of the design, so this interface must never
/// leak a renderer/engine type. State is passed as an opaque <see cref="object"/> for exactly that
/// reason (invariant 3).
/// </summary>
public interface ITerminalView
{
    /// <summary>Feeds raw PTY output bytes into the engine for parsing/rendering.</summary>
    void FeedOutput(ReadOnlyMemory<byte> data);

    /// <summary>Raised when the engine has keystrokes/paste to send toward the PTY.</summary>
    event Action<byte[]>? InputAvailable;

    /// <summary>Notifies the engine of a new terminal size (columns × rows).</summary>
    void Resize(int cols, int rows);

    /// <summary>Captures the current screen + scrollback as an opaque snapshot (engine detail).</summary>
    object GetStateSnapshot();

    /// <summary>Restores a snapshot previously produced by <see cref="GetStateSnapshot"/>.</summary>
    void RestoreState(object snapshot);

    /// <summary>Resets the engine to its pristine blank state — screen, scrollback, cursor, modes —
    /// as if freshly constructed. Used when a stopped agent's dead replay should visibly end (the
    /// stream is already gone, so no later frame can repaint the stale content).</summary>
    void Clear();
}
